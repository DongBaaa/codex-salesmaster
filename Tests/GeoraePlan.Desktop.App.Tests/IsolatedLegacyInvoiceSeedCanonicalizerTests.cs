using System.Data.Common;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GeoraePlan.Tools.SyncDiag;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class IsolatedLegacyInvoiceSeedCanonicalizerTests
{
    private const string SourceSha256 =
        "795B5A6CA153B788C6272222D778D714DB10873541775493AB7B36EA091E2FBE";
    private const string CurrentSourceSha256 =
        "E98DF3E657205319F595AE61089F50E1B87F0BD272C650827AA123B4A8616916";
    private const string LatestSourceSha256 =
        "719380E811BB04DC364FB6D2E0BD4C4E04B3D3C12F4D56207233D600F80B9A5C";
    private const string GuidPattern =
        @"(?i)\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b";
    private static readonly IsolatedLegacyInvoiceSeedCanonicalizationProfile
        SyntheticFiveGroupProfile = new(
            SourceDatabaseSha256: SourceSha256,
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
                "9424962516E516808D578D0C489BAA186B42E4F5334FFCF476CD84355B639F0E",
            AfterMetadataSha256:
                "6865ABC05DA95B2C30050C75E27A821CE6FE03DBA64894C3115AB1DCCEA44D97",
            ActiveInvoiceIdsSha256:
                "4705A8AF5BD07E0A745D718CA90F2D4B93E1B0DC59EDCA78F158D3156841B5F3",
            LatestInvoiceBusinessSha256:
                "0036AEB09F163679760DF566B048FB841BC27A7DAF2986DF1C52B20F3E34B6D7",
            DependencyReferencesSha256:
                "E4DA29E07A6077CB29822A425F3AA453E6AF19582812B2CDF86799D2E5EB83D8");

    [Fact]
    public async Task Canonicalize_RepairsFiveRealLikeGroupsAndPreservesBusinessAndDependencies()
    {
        var commandCounter = new InvoiceUpdateCommandInterceptor();
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync(commandCounter);
        var scenario = AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var before = await CaptureStateAsync(
            fixture.Db,
            scenario.AllMemberIds);
        var beforeDeletedMetadata =
            await CaptureDeletedMetadataAsync(fixture.Db);

        var report =
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeForTestsAsync(
                    fixture.Db,
                    SourceSha256);
        fixture.Db.ChangeTracker.Clear();

        var after = await CaptureStateAsync(
            fixture.Db,
            scenario.AllMemberIds);
        var afterDeletedMetadata =
            await CaptureDeletedMetadataAsync(fixture.Db);
        Assert.Equal(before.ActiveInvoiceIds, after.ActiveInvoiceIds);
        Assert.Equal(before.LatestInvoiceIds, after.LatestInvoiceIds);
        Assert.Equal(before.ProtectedInvoiceState, after.ProtectedInvoiceState);
        Assert.Equal(before.DependencyReferences, after.DependencyReferences);
        Assert.Equal(beforeDeletedMetadata, afterDeletedMetadata);
        Assert.Equal(5, commandCounter.InvoiceUpdateCount);

        Assert.True(report.Succeeded);
        Assert.Equal(2, report.SchemaVersion);
        Assert.Equal(SourceSha256, report.SourceDatabaseSha256);
        Assert.Equal(
            IsolatedLegacyInvoiceSeedCanonicalizer.ActiveOperationalSeedScope,
            report.SeedScope);
        Assert.Equal(5, report.ChangedGroupCount);
        Assert.Equal(5, report.ChangedInvoiceCount);
        Assert.Equal(2, report.ExcludedDeletedInvoiceCount);
        Assert.Equal(
            10,
            report.Groups.Sum(group => group.ActiveInvoiceCount));
        Assert.Equal(
            SyntheticFiveGroupProfile.BeforeMetadataSha256,
            report.BeforeMetadataSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.AfterMetadataSha256,
            report.AfterMetadataSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.ActiveInvoiceIdsSha256,
            report.ActiveInvoiceIdsSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.LatestInvoiceBusinessSha256,
            report.LatestInvoiceBusinessSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.DependencyReferencesSha256,
            report.DependencyReferencesSha256);
        Assert.Equal(
            "E18925B3928468BFB2763AB3F5F148D7E34FBF0C85FB836C301BA423F00D77AF",
            report.ComputeSha256());
        Assert.All(
            [
                report.BeforeMetadataSha256,
                report.AfterMetadataSha256,
                report.ActiveInvoiceIdsSha256,
                report.LatestInvoiceBusinessSha256,
                report.DependencyReferencesSha256,
                report.ComputeSha256()
            ],
            hash => Assert.Matches("^[A-F0-9]{64}$", hash));
        Assert.NotEqual(
            report.BeforeMetadataSha256,
            report.AfterMetadataSha256);
        Assert.Equal(
            [
                "deleted_predecessor_active_chain_reroot",
                "deleted_predecessor_active_chain_reroot",
                "duplicate_sibling_linearize",
                "duplicate_sibling_linearize",
                "historical_responsible_office_align"
            ],
            report.Groups.Select(group => group.Mode)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(
            [1, 2, 3, 4, 5],
            report.Groups.Select(group => group.GroupOrdinal).ToArray());
        Assert.All(
            report.Groups,
            group =>
            {
                Assert.Matches(
                    "^[A-F0-9]{64}$",
                    group.GroupFingerprintSha256);
                Assert.True(group.ActiveInvoiceCount > 0);
                Assert.InRange(
                    group.ExcludedDeletedInvoiceCount,
                    0,
                    1);
            });
        var reportJson = report.ToDeterministicJson();
        Assert.DoesNotMatch(GuidPattern, reportJson);
        Assert.DoesNotContain(
            "\"groupId\"",
            reportJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"activeInvoiceIds\":",
            reportJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"latestInvoiceId\"",
            reportJson,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "\"excludedDeletedInvoiceIds\"",
            reportJson,
            StringComparison.OrdinalIgnoreCase);

        var invoices = await fixture.Db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ToDictionaryAsync(invoice => invoice.Id);

        Assert.Equal(
            invoices[scenario.ScopeLatestId].ResponsibleOfficeCode,
            invoices[scenario.ScopeRootId].ResponsibleOfficeCode);
        Assert.Equal(
            scenario.ScopeRootId,
            invoices[scenario.ScopeRootId].VersionGroupId);
        Assert.Equal(
            scenario.ScopeRootId,
            invoices[scenario.ScopeLatestId].VersionGroupId);

        foreach (var reroot in scenario.Reroots)
        {
            var active = invoices[reroot.ActiveId];
            var deletedRoot = invoices[reroot.DeletedRootId];
            Assert.Equal(active.Id, active.VersionGroupId);
            Assert.Equal(1, active.VersionNumber);
            Assert.Null(active.PreviousVersionId);
            Assert.True(active.IsLatestVersion);
            Assert.True(deletedRoot.IsDeleted);
            Assert.False(deletedRoot.IsDirty);
            Assert.Equal(reroot.DeletedRootId, deletedRoot.VersionGroupId);
            Assert.Equal(1, deletedRoot.VersionNumber);
        }

        foreach (var branch in scenario.Branches)
        {
            var root = invoices[branch.RootId];
            var historicalSibling = invoices[branch.HistoricalSiblingId];
            var latest = invoices[branch.LatestId];
            Assert.Equal(1, root.VersionNumber);
            Assert.Null(root.PreviousVersionId);
            Assert.Equal(2, historicalSibling.VersionNumber);
            Assert.Equal(root.Id, historicalSibling.PreviousVersionId);
            Assert.Equal(3, latest.VersionNumber);
            Assert.Equal(
                historicalSibling.Id,
                latest.PreviousVersionId);
            Assert.True(latest.IsLatestVersion);
            Assert.False(historicalSibling.IsLatestVersion);
        }

        Assert.Equal(
            scenario.ExpectedPaymentCount,
            after.DependencyReferences.Count(reference =>
                reference.StartsWith(
                    "Payment|",
                    StringComparison.Ordinal)));
        Assert.Equal(
            3,
            after.DependencyReferences.Count(reference =>
                reference.StartsWith(
                    "InventoryMovement|",
                    StringComparison.Ordinal)));
        Assert.Equal(
            3,
            after.DependencyReferences.Count(reference =>
                reference.StartsWith(
                    "CostAllocation|",
                    StringComparison.Ordinal)));
        Assert.Equal(
            4,
            after.DependencyReferences.Count(reference =>
                reference.StartsWith(
                    "InvoiceRental|",
                    StringComparison.Ordinal)));
        Assert.Equal(
            2,
            after.DependencyReferences.Count(reference =>
                reference.StartsWith(
                    "TransactionRental|",
                    StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Canonicalize_UsesRevisionAndExistingLatestInsteadOfInputOrCreatedAtOrder()
    {
        await using var first = await TestDatabase.CreateInMemoryAsync();
        await using var second = await TestDatabase.CreateInMemoryAsync();
        AddDeterministicBranchScenario(
            first.Db,
            reverseInsertionOrder: false,
            reverseCreatedAtOrder: false);
        AddDeterministicBranchScenario(
            second.Db,
            reverseInsertionOrder: true,
            reverseCreatedAtOrder: true);
        await first.Db.SaveChangesAsync();
        await second.Db.SaveChangesAsync();
        first.Db.ChangeTracker.Clear();
        second.Db.ChangeTracker.Clear();

        var firstReport =
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeForTestsAsync(
                    first.Db,
                    SourceSha256);
        var secondReport =
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeForTestsAsync(
                    second.Db,
                    SourceSha256);

        Assert.Equal(
            firstReport.ToDeterministicJson(),
            secondReport.ToDeterministicJson());
        Assert.Equal(
            firstReport.ComputeSha256(),
            secondReport.ComputeSha256());
        Assert.Single(firstReport.Groups);
        Assert.Equal(
            "duplicate_sibling_linearize",
            firstReport.Groups[0].Mode);

        first.Db.ChangeTracker.Clear();
        var ordered = await first.Db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(invoice => invoice.VersionNumber)
            .ToListAsync();
        Assert.Equal(3, ordered.Count);
        Assert.Equal(G(503), ordered[^1].Id);
        Assert.True(ordered[^1].IsLatestVersion);
        Assert.Equal(G(502), ordered[^1].PreviousVersionId);
        Assert.Equal(
            ordered[1].TotalAmount,
            ordered[2].TotalAmount);
        Assert.Equal(
            ordered[1].TaxInvoiceNumber,
            ordered[2].TaxInvoiceNumber);
    }

    [Fact]
    public void ApprovedProfile_ContainsOnlyTheReviewedSourceAndExactEvidence()
    {
        var profile =
            IsolatedLegacyInvoiceSeedCanonicalizer.ApprovedProfileForTests;

        Assert.Equal(SourceSha256, profile.SourceDatabaseSha256);
        Assert.Equal(5, profile.ChangedGroupCount);
        Assert.Equal(5, profile.ChangedInvoiceCount);
        Assert.Equal(2, profile.ExcludedDeletedInvoiceCount);
        Assert.Equal(2, profile.DeletedPredecessorRerootGroupCount);
        Assert.Equal(2, profile.DuplicateSiblingGroupCount);
        Assert.Equal(1, profile.ResponsibleOfficeAlignmentGroupCount);
        Assert.Equal(
            "8A324FC2831CF3C8F996D8D6EA6B7AD01EDBFB7E793C5CB0548ED534F960904D",
            profile.BeforeMetadataSha256);
        Assert.Equal(
            "3EE8A9B5E52A2AD014AB9FFD65574D70A562E867B0C12256CA7BB7168AE1230B",
            profile.AfterMetadataSha256);
        Assert.Equal(
            "0D2CCBFEDEDA9540F4C5898187BAA7BFC3418D6272112C01772C7CE834AB076E",
            profile.ActiveInvoiceIdsSha256);
        Assert.Equal(
            "C80296708B5E84B5401D1D393CFA5FD2D117708C4B3F611BD3156330469D01EA",
            profile.LatestInvoiceBusinessSha256);
        Assert.Equal(
            "6F7DA4EFEE728601EF5AADBC60F0AB08C59DA70A3A7D49D7B74BBA652DD1ECB9",
            profile.DependencyReferencesSha256);
    }

    [Fact]
    public void ApprovedProfiles_MapAllReviewedSnapshotsToTheSameExactEvidence()
    {
        var original =
            IsolatedLegacyInvoiceSeedCanonicalizer.ApprovedProfileForTests;
        var current = IsolatedLegacyInvoiceSeedCanonicalizer
            .ApprovedProfileForSourceDatabaseSha256ForTests(
                CurrentSourceSha256);

        Assert.Equal(CurrentSourceSha256, current.SourceDatabaseSha256);
        Assert.Equal(
            original with
            {
                SourceDatabaseSha256 = CurrentSourceSha256,
                AuthorizedNonAcknowledgedOutboxCount = 3,
                AuthorizedNonAcknowledgedOutboxSha256 =
                    current.AuthorizedNonAcknowledgedOutboxSha256,
                LatestInvoiceBusinessSha256 =
                    current.LatestInvoiceBusinessSha256,
                DependencyReferencesSha256 =
                    current.DependencyReferencesSha256
            },
            current);

        var latest = IsolatedLegacyInvoiceSeedCanonicalizer
            .ApprovedProfileForSourceDatabaseSha256ForTests(
                LatestSourceSha256);
        Assert.Equal(
            current with
            {
                SourceDatabaseSha256 = LatestSourceSha256
            },
            latest);
    }

    [Fact]
    public void CurrentApprovedProfile_RequiresExactLegacyOutboxEvidence()
    {
        var original =
            IsolatedLegacyInvoiceSeedCanonicalizer.ApprovedProfileForTests;
        var current = IsolatedLegacyInvoiceSeedCanonicalizer
            .ApprovedProfileForSourceDatabaseSha256ForTests(
                CurrentSourceSha256);
        var countProperty = current.GetType().GetProperty(
            "AuthorizedNonAcknowledgedOutboxCount");
        var hashProperty = current.GetType().GetProperty(
            "AuthorizedNonAcknowledgedOutboxSha256");

        Assert.NotNull(countProperty);
        Assert.NotNull(hashProperty);
        Assert.Equal(0, countProperty.GetValue(original));
        Assert.Equal(3, countProperty.GetValue(current));
        Assert.Matches(
            "^[A-F0-9]{64}$",
            Assert.IsType<string>(hashProperty.GetValue(current)));
        Assert.DoesNotMatch(
            "^0{64}$",
            Assert.IsType<string>(hashProperty.GetValue(current)));
        Assert.Equal(
            "AA1704A42D3954DEF917EC10191B622BF4E15DA2C494C36877376D9E25125BC7",
            hashProperty.GetValue(current));
        Assert.Equal(
            "EE5B6FC6E2C9D58B3FBC066E00C95693F8EBC63DFE1BC1FCE784EB80EDF85CE8",
            current.LatestInvoiceBusinessSha256);
        Assert.Equal(
            "D5528F8C6750119E3D642C0953C8C2519CB88C1E6E37457C81868839649641F7",
            current.DependencyReferencesSha256);
        Assert.NotEqual(
            hashProperty.GetValue(original),
            hashProperty.GetValue(current));
    }

    [Fact]
    public void ProductionSyncDiagAssembly_HasNoSyntheticCanonicalizationSeam()
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var assemblyPath = RepositoryFile(
            "tools",
            "SyncDiag",
            "bin",
            configuration,
            "net8.0-windows",
            "SyncDiag.dll");
        Assert.True(File.Exists(assemblyPath), assemblyPath);
        var assembly = Assembly.LoadFile(assemblyPath);
        var canonicalizer = assembly.GetType(
            "GeoraePlan.Tools.SyncDiag.IsolatedLegacyInvoiceSeedCanonicalizer",
            throwOnError: true)!;
        var methods = canonicalizer.GetMethods(
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.Static);

        Assert.DoesNotContain(
            methods,
            method => method.Name is
                "CanonicalizeForTestsAsync" or
                "CanonicalizeWithProfileForTestsAsync" or
                "PreviewProfileForTestsAsync" or
                "BuildNonAcknowledgedOutboxEvidenceForTestsAsync" or
                "CanonicalizeTestingCoreAsync" or
                "AssertApprovedSourceDatabaseSha256ForTests" or
                "AssertDistinctInvoiceIdsForTests");
        Assert.Contains(
            methods,
            method => method.Name == "CanonicalizeTransactionAsync");
        Assert.DoesNotContain(
            methods.Where(method =>
                method.Name.Contains(
                    "Canonicalize",
                    StringComparison.Ordinal)),
            method => method.GetParameters().Any(parameter =>
                parameter.ParameterType == typeof(bool) ||
                parameter.ParameterType.Name.Contains(
                    "CanonicalizationProfile",
                    StringComparison.Ordinal) ||
                parameter.ParameterType.Name.Contains(
                    "Test",
                    StringComparison.Ordinal)));
        var canonicalizationTypes = assembly.GetTypes()
            .Where(type =>
                type.FullName?.Contains(
                    "IsolatedLegacyInvoiceSeedCanonical",
                    StringComparison.Ordinal) == true &&
                !type.IsDefined(
                    typeof(CompilerGeneratedAttribute),
                    inherit: false))
            .ToList();
        Assert.DoesNotContain(
            canonicalizationTypes,
            type =>
                type.Name.Contains("Test", StringComparison.Ordinal) ||
                typeof(Delegate).IsAssignableFrom(type));
        foreach (var type in canonicalizationTypes)
        {
            Assert.DoesNotContain(
                type.GetFields(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly),
                field =>
                    typeof(Delegate).IsAssignableFrom(field.FieldType));
            Assert.DoesNotContain(
                type.GetProperties(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly),
                property =>
                    typeof(Delegate).IsAssignableFrom(
                        property.PropertyType));
            Assert.DoesNotContain(
                type.GetMethods(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance |
                    BindingFlags.Static |
                    BindingFlags.DeclaredOnly),
                method =>
                    typeof(Delegate).IsAssignableFrom(method.ReturnType) ||
                    method.GetParameters().Any(parameter =>
                        typeof(Delegate).IsAssignableFrom(
                            parameter.ParameterType)));
            Assert.DoesNotContain(
                type.GetConstructors(
                    BindingFlags.Public |
                    BindingFlags.NonPublic |
                    BindingFlags.Instance),
                constructor => constructor.GetParameters().Any(parameter =>
                    typeof(Delegate).IsAssignableFrom(
                        parameter.ParameterType)));
        }
    }

    [Fact]
    public void ProductionSource_HasNoFailurePointAfterTransactionCommit()
    {
        var source = File.ReadAllText(
            RepositoryFile(
                "tools",
                "SyncDiag",
                "IsolatedLegacyInvoiceSeedCanonicalizer.cs"));
        var publicWrapper = Between(
            source,
            "public static async Task<IsolatedLegacyInvoiceSeedCanonicalizationResult>",
            "#if GEORAEPLAN_CANONICALIZER_TESTING");
        Assert.Contains(
            "return await CanonicalizeTransactionAsync(",
            publicWrapper,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "AssertStable()",
            publicWrapper,
            StringComparison.Ordinal);

        var transactionBody = Between(
            source,
            "CanonicalizeTransactionAsync(",
            "private static CanonicalizationPlan BuildPlan(");
        var stabilityIndex = transactionBody.LastIndexOf(
            "AssertStable();",
            StringComparison.Ordinal);
        var commitIndex = transactionBody.LastIndexOf(
            "await transaction.CommitAsync(cancellationToken);",
            StringComparison.Ordinal);
        var returnIndex = transactionBody.LastIndexOf(
            "return result;",
            StringComparison.Ordinal);
        Assert.True(stabilityIndex >= 0);
        Assert.True(commitIndex > stabilityIndex);
        Assert.True(returnIndex > commitIndex);
        Assert.DoesNotContain(
            "AssertStable();",
            transactionBody[(commitIndex +
                "await transaction.CommitAsync(cancellationToken);".Length)..],
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommandPreflight_RejectionPreservesBytesSchemaAndSettings()
    {
        var root = Path.Combine(
            @"D:\DevCaches\georaeplan-v1-tests\canonicalizer-preflight",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "preflight.db");
        await using (var fixture =
                     await TestDatabase.OpenFileAsync(databasePath))
        {
            fixture.Db.Settings.Add(new LocalSetting
            {
                Key = "preflight-sentinel",
                Value = "unchanged"
            });
            await fixture.Db.SaveChangesAsync();
        }
        var beforeSha256 = ComputeFileSha256(databasePath);
        var beforeState =
            await CaptureSchemaAndSettingsAsync(databasePath);
        var program = File.ReadAllText(
            RepositoryFile("tools", "SyncDiag", "Program.cs"));
        var authorizationIndex = program.IndexOf(
            "AcquireAuthorization(",
            StringComparison.Ordinal);
        var contextIndex = program.IndexOf(
            "new LocalDbContext()",
            StringComparison.Ordinal);
        var initializerIndex = program.IndexOf(
            "LocalDbInitializer.InitializeAsync(db)",
            StringComparison.Ordinal);
        Assert.InRange(authorizationIndex, 0, contextIndex - 1);
        Assert.InRange(contextIndex, authorizationIndex + 1, initializerIndex - 1);

        var oldTestMode =
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_MODE");
        var oldSeedMode =
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_MODE");
        var oldOptIn = Environment.GetEnvironmentVariable(
            IsolatedLegacyInvoiceSeedCanonicalizer
                .ExplicitOptInEnvironmentKey);
        var oldSource = Environment.GetEnvironmentVariable(
            IsolatedLegacyInvoiceSeedCanonicalizer
                .SourceDatabaseSha256EnvironmentKey);
        try
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_MODE",
                "1");
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_SEED_MODE",
                "1");
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .ExplicitOptInEnvironmentKey,
                null);
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .SourceDatabaseSha256EnvironmentKey,
                SourceSha256);
            Assert.Throws<InvalidOperationException>(
                () => IsolatedLegacyInvoiceSeedCanonicalizer
                    .AssertAuthorizationEnvironmentForTests(
                        JsonSerializer.Serialize(new
                        {
                            schemaVersion = 1,
                            databaseSha256 = SourceSha256
                        })));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_MODE",
                oldTestMode);
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_SEED_MODE",
                oldSeedMode);
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .ExplicitOptInEnvironmentKey,
                oldOptIn);
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .SourceDatabaseSha256EnvironmentKey,
                oldSource);
        }

        Assert.Equal(beforeSha256, ComputeFileSha256(databasePath));
        Assert.Equal(
            beforeState,
            await CaptureSchemaAndSettingsAsync(databasePath));
    }

    [Fact]
    public async Task CommandOutputFailure_CommitsOnceAndRetryRecoversWithoutMutation()
    {
        var program = File.ReadAllText(
            RepositoryFile("tools", "SyncDiag", "Program.cs"));
        var committedIndex = program.IndexOf(
            "canonicalizationCommitted = true;",
            StringComparison.Ordinal);
        var outputIndex = program.IndexOf(
            ".WriteAdvisoryCommandOutput(",
            StringComparison.Ordinal);
        Assert.InRange(committedIndex, 0, outputIndex - 1);
        Assert.Contains(
            "if (canonicalizationCommitted)",
            program,
            StringComparison.Ordinal);

        var root = Path.Combine(
            @"D:\DevCaches\georaeplan-v1-tests\canonicalizer-output-recovery",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var recoveryPath = Path.Combine(
            root,
            IsolatedLegacyInvoiceSeedCanonicalizer
                .RecoveryResultFileName);
        var commandCounter = new InvoiceUpdateCommandInterceptor();
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync(commandCounter);
        AddFiveGroupScenario(fixture.Db);
        fixture.Db.Settings.Add(new LocalSetting
        {
            Key = "output-retry-sentinel",
            Value = "unchanged"
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var firstReport =
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    fixture.Db,
                    SyntheticFiveGroupProfile,
                    recoveryResultPath: recoveryPath);
        Assert.Equal(5, commandCounter.InvoiceUpdateCount);
        var committedState =
            await CaptureCanonicalizationMetadataAsync(fixture.Db);
        var settingsBeforeRetry = await fixture.Db.Settings
            .AsNoTracking()
            .OrderBy(setting => setting.Key)
            .Select(setting => $"{setting.Key}={setting.Value}")
            .ToArrayAsync();
        var result = IsolatedLegacyInvoiceSeedCanonicalizer
            .BuildResultForTests(firstReport);

        var outputFailure = Record.Exception(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .WriteAdvisoryCommandOutput(
                    new ThrowingTextWriter(),
                    result));
        Assert.Null(outputFailure);

        fixture.Db.ChangeTracker.Clear();
        var recoveredReport =
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    fixture.Db,
                    SyntheticFiveGroupProfile,
                    recoveryResultPath: recoveryPath);
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(
            firstReport.ToDeterministicJson(),
            recoveredReport.ToDeterministicJson());
        Assert.Equal(5, commandCounter.InvoiceUpdateCount);
        Assert.Equal(
            committedState,
            await CaptureCanonicalizationMetadataAsync(fixture.Db));
        Assert.Equal(
            settingsBeforeRetry,
            await fixture.Db.Settings
                .AsNoTracking()
                .OrderBy(setting => setting.Key)
                .Select(setting => $"{setting.Key}={setting.Value}")
                .ToArrayAsync());
    }

    [Fact]
    public async Task RecoveryArtifact_PreplantedHardLinkNeverWritesVictimAndPublishesPrivateFile()
    {
        var root = NewRecoveryRoot("hardlink");
        var recoveryPath = RecoveryPath(root);
        var victimPath = Path.Combine(root, "victim.txt");
        var victimBytes = Encoding.UTF8.GetBytes(
            "must-never-be-truncated-or-overwritten");
        await File.WriteAllBytesAsync(victimPath, victimBytes);
        Assert.True(
            CreateHardLinkW(
                recoveryPath,
                victimPath,
                IntPtr.Zero),
            new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error()).Message);

        await using var fixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        await IsolatedLegacyInvoiceSeedCanonicalizer
            .CanonicalizeWithProfileForTestsAsync(
                fixture.Db,
                SyntheticFiveGroupProfile,
                recoveryResultPath: recoveryPath);

        Assert.Equal(
            victimBytes,
            await File.ReadAllBytesAsync(victimPath));
        Assert.NotEqual(
            ComputeFileSha256(victimPath),
            ComputeFileSha256(recoveryPath));
        var currentUser = WindowsIdentity.GetCurrent().User!;
        var security = new FileInfo(recoveryPath).GetAccessControl(
            AccessControlSections.Access |
            AccessControlSections.Owner);
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            currentUser.Value,
            security.GetOwner(typeof(SecurityIdentifier))!.Value);
        var rule = Assert.Single(
            security.GetAccessRules(
                    includeExplicit: true,
                    includeInherited: true,
                    typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>());
        Assert.False(rule.IsInherited);
        Assert.Equal(
            AccessControlType.Allow,
            rule.AccessControlType);
        Assert.Equal(
            FileSystemRights.FullControl,
            rule.FileSystemRights);
        Assert.Equal(
            currentUser.Value,
            rule.IdentityReference.Value);
    }

    [Fact]
    public async Task RecoveryArtifact_ReparseRaceFailsBeforeCommitAndPreservesTarget()
    {
        var root = NewRecoveryRoot("reparse-race");
        var recoveryPath = RecoveryPath(root);
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var before =
            await CaptureCanonicalizationMetadataAsync(fixture.Db);

        var error = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    fixture.Db,
                    SyntheticFiveGroupProfile,
                    fault:
                        IsolatedLegacyInvoiceSeedCanonicalizationFault
                            .CreateRecoveryArtifactReparseBeforePublish,
                    recoveryResultPath: recoveryPath));
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal("recovery_artifact_path_unsafe", error.ReasonCode);
        Assert.Equal(
            "unchanged",
            await File.ReadAllTextAsync(
                Path.Combine(
                    $"{recoveryPath}.race-target",
                    "sentinel.txt"),
                Encoding.UTF8));
        Assert.Equal(
            before,
            await CaptureCanonicalizationMetadataAsync(fixture.Db));
    }

    [Fact]
    public async Task RecoveryArtifact_PartialWritePreservesPriorArtifactAndRollsBack()
    {
        var root = NewRecoveryRoot("partial-write");
        var recoveryPath = RecoveryPath(root);
        await using (var first =
                     await TestDatabase.CreateInMemoryAsync())
        {
            AddFiveGroupScenario(first.Db);
            await first.Db.SaveChangesAsync();
            first.Db.ChangeTracker.Clear();
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    first.Db,
                    SyntheticFiveGroupProfile,
                    recoveryResultPath: recoveryPath);
        }
        var priorArtifactSha256 = ComputeFileSha256(recoveryPath);

        await using var retry =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(retry.Db);
        await retry.Db.SaveChangesAsync();
        retry.Db.ChangeTracker.Clear();
        var before =
            await CaptureCanonicalizationMetadataAsync(retry.Db);

        await Assert.ThrowsAsync<InjectedCanonicalizationFaultException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    retry.Db,
                    SyntheticFiveGroupProfile,
                    fault:
                        IsolatedLegacyInvoiceSeedCanonicalizationFault
                            .ThrowDuringRecoveryArtifactWrite,
                    recoveryResultPath: recoveryPath));
        retry.Db.ChangeTracker.Clear();

        Assert.Equal(
            priorArtifactSha256,
            ComputeFileSha256(recoveryPath));
        Assert.Equal(
            before,
            await CaptureCanonicalizationMetadataAsync(retry.Db));
    }

    [Fact]
    public async Task Transaction_PostcommitDisposeFailureIsAdvisoryAndRetryIsIdempotent()
    {
        var root = NewRecoveryRoot("postcommit-dispose");
        var recoveryPath = RecoveryPath(root);
        var commandCounter = new InvoiceUpdateCommandInterceptor();
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync(commandCounter);
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var first = await IsolatedLegacyInvoiceSeedCanonicalizer
            .CanonicalizeWithProfileForTestsAsync(
                fixture.Db,
                SyntheticFiveGroupProfile,
                fault:
                    IsolatedLegacyInvoiceSeedCanonicalizationFault
                        .ThrowDuringPostcommitDispose,
                recoveryResultPath: recoveryPath);
        fixture.Db.ChangeTracker.Clear();
        var committed =
            await CaptureCanonicalizationMetadataAsync(fixture.Db);
        var recovered = await IsolatedLegacyInvoiceSeedCanonicalizer
            .CanonicalizeWithProfileForTestsAsync(
                fixture.Db,
                SyntheticFiveGroupProfile,
                recoveryResultPath: recoveryPath);
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(5, commandCounter.InvoiceUpdateCount);
        Assert.Equal(
            first.ToDeterministicJson(),
            recovered.ToDeterministicJson());
        Assert.Equal(
            committed,
            await CaptureCanonicalizationMetadataAsync(fixture.Db));
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("succeeded")]
    [InlineData("scope")]
    [InlineData("group-count")]
    [InlineData("invoice-count")]
    [InlineData("deleted-count")]
    [InlineData("before-hash")]
    [InlineData("after-hash")]
    [InlineData("active-hash")]
    [InlineData("latest-hash")]
    [InlineData("dependency-hash")]
    [InlineData("group-removed")]
    [InlineData("group-mode")]
    [InlineData("group-ordinal")]
    [InlineData("group-field-set")]
    [InlineData("group-fingerprint")]
    [InlineData("group-before-hash")]
    [InlineData("group-after-hash")]
    [InlineData("group-active-count")]
    [InlineData("group-deleted-count")]
    public async Task RecoveryArtifact_AuthenticatedReportRejectsTamperedContract(
        string mutation)
    {
        var root = NewRecoveryRoot($"tamper-{mutation}");
        var recoveryPath = RecoveryPath(root);
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        await IsolatedLegacyInvoiceSeedCanonicalizer
            .CanonicalizeWithProfileForTestsAsync(
                fixture.Db,
                SyntheticFiveGroupProfile,
                recoveryResultPath: recoveryPath);
        TamperAuthenticatedRecoveryArtifact(
            recoveryPath,
            mutation);
        fixture.Db.ChangeTracker.Clear();

        var error = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    fixture.Db,
                    SyntheticFiveGroupProfile,
                    recoveryResultPath: recoveryPath));

        Assert.Equal(
            "committed_result_recovery_artifact_mismatch",
            error.ReasonCode);
    }

    [Fact]
    public async Task RecoveryArtifact_CrossRootAndCrossSourceReplayFailClosed()
    {
        var sourceRoot = NewRecoveryRoot("replay-source");
        var sourcePath = RecoveryPath(sourceRoot);
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        await IsolatedLegacyInvoiceSeedCanonicalizer
            .CanonicalizeWithProfileForTestsAsync(
                fixture.Db,
                SyntheticFiveGroupProfile,
                recoveryResultPath: sourcePath);

        var replayRoot = NewRecoveryRoot("replay-target");
        var replayPath = RecoveryPath(replayRoot);
        File.Copy(sourcePath, replayPath);
        fixture.Db.ChangeTracker.Clear();
        var rootError = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    fixture.Db,
                    SyntheticFiveGroupProfile,
                    recoveryResultPath: replayPath));
        Assert.Equal(
            "committed_result_recovery_artifact_mismatch",
            rootError.ReasonCode);

        fixture.Db.ChangeTracker.Clear();
        var sourceError = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    fixture.Db,
                    SyntheticFiveGroupProfile with
                    {
                        SourceDatabaseSha256 =
                            DifferentSha256(SourceSha256)
                    },
                    recoveryResultPath: sourcePath));
        Assert.Equal(
            "committed_result_recovery_artifact_mismatch",
            sourceError.ReasonCode);
    }

    [Fact]
    public void ApprovedSourceDatabaseSha256_RejectsANonApprovedValidHash()
    {
        IsolatedLegacyInvoiceSeedCanonicalizer
            .AssertApprovedSourceDatabaseSha256ForTests(SourceSha256);
        IsolatedLegacyInvoiceSeedCanonicalizer
            .AssertApprovedSourceDatabaseSha256ForTests(
                CurrentSourceSha256.ToLowerInvariant());
        IsolatedLegacyInvoiceSeedCanonicalizer
            .AssertApprovedSourceDatabaseSha256ForTests(
                LatestSourceSha256.ToLowerInvariant());
        var error = Assert.Throws<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .AssertApprovedSourceDatabaseSha256ForTests(
                    new string('A', 64)));

        Assert.Equal(
            "source_database_sha256_not_approved",
            error.ReasonCode);
        Assert.DoesNotMatch(GuidPattern, error.Message);
    }

    [Fact]
    public void ProfileInspectionAuthorization_AllowsAnAttestedUnapprovedHashOnlyWithExplicitReadOnlyOptIn()
    {
        var unapprovedSha256 = new string('A', 64);
        var attestation = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            databaseSha256 = unapprovedSha256
        });
        var oldTestMode =
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_MODE");
        var oldSeedMode =
            Environment.GetEnvironmentVariable("GEORAEPLAN_TEST_SEED_MODE");
        var oldInspection = Environment.GetEnvironmentVariable(
            IsolatedLegacyInvoiceSeedCanonicalizer
                .ProfileInspectionOptInEnvironmentKey);
        var oldSource = Environment.GetEnvironmentVariable(
            IsolatedLegacyInvoiceSeedCanonicalizer
                .SourceDatabaseSha256EnvironmentKey);
        try
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_MODE",
                "1");
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_SEED_MODE",
                "1");
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .ProfileInspectionOptInEnvironmentKey,
                "1");
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .SourceDatabaseSha256EnvironmentKey,
                unapprovedSha256);

            Assert.Equal(
                unapprovedSha256,
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .AssertProfileInspectionEnvironmentForTests(
                        attestation));

            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .ProfileInspectionOptInEnvironmentKey,
                null);
            Assert.Throws<InvalidOperationException>(() =>
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .AssertProfileInspectionEnvironmentForTests(
                        attestation));
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_MODE",
                oldTestMode);
            Environment.SetEnvironmentVariable(
                "GEORAEPLAN_TEST_SEED_MODE",
                oldSeedMode);
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .ProfileInspectionOptInEnvironmentKey,
                oldInspection);
            Environment.SetEnvironmentVariable(
                IsolatedLegacyInvoiceSeedCanonicalizer
                    .SourceDatabaseSha256EnvironmentKey,
                oldSource);
        }
    }

    [Fact]
    public async Task ProfileGuard_AcceptsAnExactSyntheticProfile()
    {
        await using var guardedFixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(guardedFixture.Db);
        await guardedFixture.Db.SaveChangesAsync();
        guardedFixture.Db.ChangeTracker.Clear();

        var guardedReport =
            await IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    guardedFixture.Db,
                    SyntheticFiveGroupProfile);

        Assert.Equal(
            SyntheticFiveGroupProfile.BeforeMetadataSha256,
            guardedReport.BeforeMetadataSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.AfterMetadataSha256,
            guardedReport.AfterMetadataSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.ActiveInvoiceIdsSha256,
            guardedReport.ActiveInvoiceIdsSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.LatestInvoiceBusinessSha256,
            guardedReport.LatestInvoiceBusinessSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.DependencyReferencesSha256,
            guardedReport.DependencyReferencesSha256);
    }

    [Fact]
    public async Task ProfilePreview_IsReadOnlyAndMatchesTheSyntheticProfile()
    {
        await using var fixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var before = await CaptureCanonicalizationMetadataAsync(
            fixture.Db);

        var preview = await IsolatedLegacyInvoiceSeedCanonicalizer
            .PreviewProfileForTestsAsync(
                fixture.Db,
                SourceSha256);
        fixture.Db.ChangeTracker.Clear();

        Assert.Equal(1, preview.SchemaVersion);
        Assert.Equal(SourceSha256, preview.SourceDatabaseSha256);
        Assert.Equal(0, preview.AuthorizedNonAcknowledgedOutboxCount);
        Assert.Equal(
            SyntheticFiveGroupProfile
                .AuthorizedNonAcknowledgedOutboxSha256,
            preview.AuthorizedNonAcknowledgedOutboxSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.ChangedGroupCount,
            preview.ChangedGroupCount);
        Assert.Equal(
            SyntheticFiveGroupProfile.ChangedInvoiceCount,
            preview.ChangedInvoiceCount);
        Assert.Equal(
            SyntheticFiveGroupProfile.BeforeMetadataSha256,
            preview.BeforeMetadataSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.AfterMetadataSha256,
            preview.ProjectedAfterMetadataSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.ActiveInvoiceIdsSha256,
            preview.ActiveInvoiceIdsSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.LatestInvoiceBusinessSha256,
            preview.LatestInvoiceBusinessSha256);
        Assert.Equal(
            SyntheticFiveGroupProfile.DependencyReferencesSha256,
            preview.DependencyReferencesSha256);
        Assert.DoesNotMatch(GuidPattern, preview.ToDeterministicJson());
        Assert.Equal(
            before,
            await CaptureCanonicalizationMetadataAsync(fixture.Db));
    }

    [Fact]
    public async Task ProfileGuard_AllowsOnlyExactAuthorizedLegacyOutboxEvidence()
    {
        await using var acceptedFixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(acceptedFixture.Db);
        acceptedFixture.Db.SyncOutboxEntries.Add(
            NewAuthorizedLegacyOutbox("approved-shape"));
        await acceptedFixture.Db.SaveChangesAsync();
        acceptedFixture.Db.ChangeTracker.Clear();
        var evidence = await IsolatedLegacyInvoiceSeedCanonicalizer
            .BuildNonAcknowledgedOutboxEvidenceForTestsAsync(
                acceptedFixture.Db);
        var profile = SyntheticFiveGroupProfile with
        {
            AuthorizedNonAcknowledgedOutboxCount = evidence.Count,
            AuthorizedNonAcknowledgedOutboxSha256 = evidence.Sha256
        };

        var report = await IsolatedLegacyInvoiceSeedCanonicalizer
            .CanonicalizeWithProfileForTestsAsync(
                acceptedFixture.Db,
                profile);
        acceptedFixture.Db.ChangeTracker.Clear();

        Assert.True(report.Succeeded);
        Assert.Equal(1, await acceptedFixture.Db.SyncOutboxEntries
            .CountAsync(entry => entry.Status != "Acknowledged"));

        await using var rejectedFixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(rejectedFixture.Db);
        rejectedFixture.Db.SyncOutboxEntries.Add(
            NewAuthorizedLegacyOutbox("different-shape"));
        await rejectedFixture.Db.SaveChangesAsync();
        rejectedFixture.Db.ChangeTracker.Clear();
        var before = await CaptureCanonicalizationMetadataAsync(
            rejectedFixture.Db);

        var error = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    rejectedFixture.Db,
                    profile));
        rejectedFixture.Db.ChangeTracker.Clear();

        Assert.Equal(
            "approved_partial_push_outbox_mismatch",
            error.ReasonCode);
        Assert.Matches("^[A-F0-9]{64}$", error.EvidenceSha256);
        Assert.Equal(
            before,
            await CaptureCanonicalizationMetadataAsync(
                rejectedFixture.Db));
        Assert.Equal(1, await rejectedFixture.Db.SyncOutboxEntries
            .CountAsync(entry => entry.Status != "Acknowledged"));
    }

    [Fact]
    public async Task ProfileGuard_RejectsAValidSixthGroupBeforeAnyUpdate()
    {
        await using var guardedFixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(guardedFixture.Db);
        AddDeterministicBranchScenario(
            guardedFixture.Db,
            reverseInsertionOrder: false,
            reverseCreatedAtOrder: false);
        await guardedFixture.Db.SaveChangesAsync();
        guardedFixture.Db.ChangeTracker.Clear();
        var before =
            await CaptureCanonicalizationMetadataAsync(guardedFixture.Db);

        var error = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    guardedFixture.Db,
                    SyntheticFiveGroupProfile));
        guardedFixture.Db.ChangeTracker.Clear();
        var after =
            await CaptureCanonicalizationMetadataAsync(guardedFixture.Db);

        Assert.Equal(
            "required_changed_group_count_mismatch",
            error.ReasonCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task ProfileGuard_RollsBackUpdatesWhenAfterEvidenceDoesNotMatch()
    {
        var invalidAfterProfile = SyntheticFiveGroupProfile with
        {
            AfterMetadataSha256 =
                DifferentSha256(
                    SyntheticFiveGroupProfile.AfterMetadataSha256)
        };

        await using var guardedFixture =
            await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(guardedFixture.Db);
        await guardedFixture.Db.SaveChangesAsync();
        guardedFixture.Db.ChangeTracker.Clear();
        var before =
            await CaptureCanonicalizationMetadataAsync(guardedFixture.Db);

        var error = await Assert.ThrowsAsync<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeWithProfileForTestsAsync(
                    guardedFixture.Db,
                    invalidAfterProfile));
        guardedFixture.Db.ChangeTracker.Clear();
        var after =
            await CaptureCanonicalizationMetadataAsync(guardedFixture.Db);

        Assert.Equal("required_after_metadata_mismatch", error.ReasonCode);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Transaction_RollsBackWhenCancellationIsInjectedAfterFirstUpdate()
    {
        await using var fixture = await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var before = await CaptureCanonicalizationMetadataAsync(fixture.Db);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeForTestsAsync(
                    fixture.Db,
                    SourceSha256,
                    fault:
                        IsolatedLegacyInvoiceSeedCanonicalizationFault
                            .CancelAfterFirstUpdate));
        fixture.Db.ChangeTracker.Clear();
        var after = await CaptureCanonicalizationMetadataAsync(fixture.Db);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Transaction_RollsBackWhenExceptionIsInjectedAfterFirstUpdate()
    {
        await using var fixture = await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var before = await CaptureCanonicalizationMetadataAsync(fixture.Db);
        await Assert.ThrowsAsync<InjectedCanonicalizationFaultException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeForTestsAsync(
                    fixture.Db,
                    SourceSha256,
                    fault:
                        IsolatedLegacyInvoiceSeedCanonicalizationFault
                            .ThrowAfterFirstUpdate));
        fixture.Db.ChangeTracker.Clear();
        var after = await CaptureCanonicalizationMetadataAsync(fixture.Db);

        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Transaction_RollsBackWhenPrecommitGuardThrows()
    {
        await using var fixture = await TestDatabase.CreateInMemoryAsync();
        AddFiveGroupScenario(fixture.Db);
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();
        var before = await CaptureCanonicalizationMetadataAsync(fixture.Db);
        await Assert.ThrowsAsync<InjectedCanonicalizationFaultException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .CanonicalizeForTestsAsync(
                    fixture.Db,
                    SourceSha256,
                    fault:
                        IsolatedLegacyInvoiceSeedCanonicalizationFault
                            .ThrowBeforeCommit));
        fixture.Db.ChangeTracker.Clear();
        var after = await CaptureCanonicalizationMetadataAsync(fixture.Db);

        Assert.Equal(before, after);
    }

    [Fact]
    public void Rejection_RedactsRawGroupIdentifiers()
    {
        var groupId = G(7654321);
        var error =
            new IsolatedLegacyInvoiceSeedCanonicalizationException(
                "synthetic_rejection",
                groupId);

        Assert.DoesNotContain(
            FormatId(groupId),
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(GuidPattern, error.Message);
        Assert.Matches(
            "^[A-F0-9]{64}$",
            error.GroupFingerprintSha256);
    }

    [Theory]
    [InlineData("latest_missing", "active_latest_missing")]
    [InlineData("latest_multiple", "active_latest_not_unique")]
    [InlineData("cycle", "invoice_version_cycle")]
    [InlineData("external_predecessor", "external_predecessor")]
    [InlineData("disconnected", "disconnected_active_chain")]
    [InlineData("revision_zero", "nonlatest_revision_not_positive")]
    [InlineData("revision_tie", "nonlatest_revision_not_unique")]
    [InlineData("branch_width", "sibling_branch_width_exceeded")]
    [InlineData("latest_customer_deleted", "latest_customer_missing_or_deleted")]
    [InlineData("protected_scope", "protected_scope_alignment_required")]
    [InlineData("rental_profile_missing", "rental_profile_missing_or_deleted")]
    [InlineData("inventory_line_mismatch", "inventory_invoice_line_mismatch")]
    [InlineData("deleted_dependency", "deleted_invoice_line_dependency")]
    [InlineData("partial_push", "partial_push_outbox_present")]
    public async Task Canonicalize_FailsClosedWithoutChangingDatabaseFileOrSyncState(
        string failureMode,
        string expectedReasonCode)
    {
        var databasePath =
            await CreateInvalidFileDatabaseAsync(failureMode);
        var beforeSha256 = ComputeFileSha256(databasePath);
        var beforeLogicalState =
            await CaptureFailureStateAsync(databasePath);

        IsolatedLegacyInvoiceSeedCanonicalizationException error;
        await using (var fixture =
                     await TestDatabase.OpenFileAsync(databasePath))
        {
            error = await Assert.ThrowsAsync<
                IsolatedLegacyInvoiceSeedCanonicalizationException>(
                () => IsolatedLegacyInvoiceSeedCanonicalizer
                    .CanonicalizeForTestsAsync(
                        fixture.Db,
                        SourceSha256));
        }

        var afterSha256 = ComputeFileSha256(databasePath);
        var afterLogicalState =
            await CaptureFailureStateAsync(databasePath);
        Assert.Equal(expectedReasonCode, error.ReasonCode);
        Assert.Equal(beforeSha256, afterSha256);
        Assert.Equal(beforeLogicalState, afterLogicalState);
    }

    [Fact]
    public void Planner_RejectsDuplicateInvoiceIdsBeforeAnyDatabaseMutation()
    {
        var duplicateId = G(700);
        var invoices = new[]
        {
            NewInvoice(
                duplicateId,
                duplicateId,
                1,
                null,
                latest: true,
                deleted: false,
                G(9700),
                revision: 1,
                responsibleOffice: "USENET"),
            NewInvoice(
                duplicateId,
                duplicateId,
                2,
                duplicateId,
                latest: false,
                deleted: false,
                G(9700),
                revision: 2,
                responsibleOffice: "USENET")
        };

        var error = Assert.Throws<
            IsolatedLegacyInvoiceSeedCanonicalizationException>(
            () => IsolatedLegacyInvoiceSeedCanonicalizer
                .AssertDistinctInvoiceIdsForTests(invoices));

        Assert.Equal("duplicate_invoice_id", error.ReasonCode);
    }

    [Fact]
    public void CommandAndPreparationScript_RequireExplicitFreshSnapshotLeaseAndLoopbackBinding()
    {
        var canonicalizer = File.ReadAllText(
            RepositoryFile(
                "tools",
                "SyncDiag",
                "IsolatedLegacyInvoiceSeedCanonicalizer.cs"));
        var program = File.ReadAllText(
            RepositoryFile(
                "tools",
                "SyncDiag",
                "Program.cs"));
        var preparation = File.ReadAllText(
            RepositoryFile(
                "테스트 시행",
                "테스트-환경-준비.ps1"));

        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_CANONICALIZE_LEGACY_INVOICES",
            canonicalizer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_SOURCE_DATABASE_SHA256",
            canonicalizer,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SEED_INSPECT_LEGACY_INVOICE_PROFILE",
            canonicalizer,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-isolated-seed-source-attestation.json",
            canonicalizer,
            StringComparison.Ordinal);
        Assert.Contains(
            "partial_push_outbox_present",
            canonicalizer,
            StringComparison.Ordinal);
        Assert.Contains(
            "active_operational_seed_only_not_deleted_history_migration",
            canonicalizer,
            StringComparison.Ordinal);

        var updateSql = Between(
            canonicalizer,
            "UPDATE \"Invoices\"",
            "WHERE \"Id\"");
        Assert.Contains("\"VersionGroupId\"", updateSql, StringComparison.Ordinal);
        Assert.Contains("\"VersionNumber\"", updateSql, StringComparison.Ordinal);
        Assert.Contains("\"PreviousVersionId\"", updateSql, StringComparison.Ordinal);
        Assert.Contains("\"ResponsibleOfficeCode\"", updateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"CustomerId\"", updateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TenantCode\"", updateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"OfficeCode\"", updateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TaxInvoiceNumber\"", updateSql, StringComparison.Ordinal);
        Assert.DoesNotContain("\"InvoiceNumber\"", updateSql, StringComparison.Ordinal);

        Assert.Contains(
            "canonicalize-legacy-invoice-test-seed",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "inspect-legacy-invoice-test-seed-profile",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "PreviewUnapprovedProfileAsync",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "RequiresIsolatedTestServerTargetGuard",
            program,
            StringComparison.Ordinal);
        Assert.Contains("baseUri.IsLoopback", program, StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SERVER_ROOT",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            "GEORAEPLAN_TEST_SERVER_BASEURL",
            program,
            StringComparison.Ordinal);
        Assert.Contains(
            ".georaeplan-isolated-server-root",
            program,
            StringComparison.Ordinal);

        Assert.Contains(
            "[switch]$CanonicalizeLegacyInvoiceSeed",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "-SkipDataCopy is not allowed",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "CanonicalizeLegacyInvoiceSeedExpectedSourceDatabaseSha256",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "legacy-invoice-seed-canonicalization.json",
            preparation,
            StringComparison.Ordinal);
        Assert.Contains(
            "legacy-invoice-seed-canonicalization.success",
            preparation,
            StringComparison.Ordinal);

        var seedFunction = preparation[
            preparation.IndexOf(
                "function Initialize-IsolatedServerData",
                StringComparison.Ordinal)..];
        var prepareIndex = seedFunction.IndexOf(
            "'prepare-test-seed'",
            StringComparison.Ordinal);
        var canonicalizeIndex = seedFunction.IndexOf(
            "'canonicalize-legacy-invoice-test-seed'",
            StringComparison.Ordinal);
        var markDirtyIndex = seedFunction.IndexOf(
            "'mark-all-dirty'",
            StringComparison.Ordinal);
        Assert.True(prepareIndex >= 0);
        Assert.True(canonicalizeIndex > prepareIndex);
        Assert.True(markDirtyIndex > canonicalizeIndex);
        Assert.Equal(
            1,
            CountOccurrences(
                seedFunction,
                "'canonicalize-legacy-invoice-test-seed'"));
    }

    private static FiveGroupScenario AddFiveGroupScenario(
        LocalDbContext db)
    {
        var customer = NewCustomer(G(9001));
        db.Customers.Add(customer);
        var rentalProfile1 = NewRentalProfile(
            G(9101),
            customer.Id);
        var rentalProfile2 = NewRentalProfile(
            G(9102),
            customer.Id);
        db.RentalBillingProfiles.AddRange(
            rentalProfile1,
            rentalProfile2);

        var scopeRootId = G(101);
        var scopeLatestId = G(102);
        var scopeRoot = NewInvoice(
            scopeRootId,
            scopeRootId,
            1,
            null,
            latest: false,
            deleted: false,
            customer.Id,
            revision: 112,
            responsibleOffice: "YEONSU");
        var scopeLatest = NewInvoice(
            scopeLatestId,
            scopeRootId,
            2,
            scopeRootId,
            latest: true,
            deleted: false,
            customer.Id,
            revision: 111,
            responsibleOffice: "USENET");

        var reroots = new List<RerootIds>();
        var rerootInvoices = new List<LocalInvoice>();
        for (var index = 0; index < 2; index++)
        {
            var deletedRootId = G(201 + (index * 10));
            var activeId = G(202 + (index * 10));
            var profile = index == 0
                ? rentalProfile1
                : rentalProfile2;
            var runId = G(9201 + index);
            var deletedRoot = NewInvoice(
                deletedRootId,
                deletedRootId,
                1,
                null,
                latest: false,
                deleted: true,
                customer.Id,
                revision: 200 + index,
                responsibleOffice: "USENET");
            deletedRoot.IsDirty = false;
            deletedRoot.LinkedRentalBillingProfileId =
                profile.Id;
            deletedRoot.LinkedRentalBillingRunId = runId;
            var active = NewInvoice(
                activeId,
                deletedRootId,
                2,
                deletedRootId,
                latest: true,
                deleted: false,
                customer.Id,
                revision: 210 + index,
                responsibleOffice: "USENET");
            active.LinkedRentalBillingProfileId = profile.Id;
            active.LinkedRentalBillingRunId = runId;
            rerootInvoices.AddRange([deletedRoot, active]);
            reroots.Add(new RerootIds(
                deletedRootId,
                activeId,
                profile.Id,
                runId));
        }

        var branches = new List<BranchIds>();
        var branchInvoices = new List<LocalInvoice>();
        for (var index = 0; index < 2; index++)
        {
            var rootId = G(301 + (index * 10));
            var historicalId = G(302 + (index * 10));
            var latestId = G(303 + (index * 10));
            var root = NewInvoice(
                rootId,
                rootId,
                1,
                null,
                latest: false,
                deleted: false,
                customer.Id,
                revision: 300 + (index * 10),
                responsibleOffice: "USENET");
            var historical = NewInvoice(
                historicalId,
                rootId,
                2,
                rootId,
                latest: false,
                deleted: false,
                customer.Id,
                revision: 301 + (index * 10),
                responsibleOffice: "USENET");
            historical.TotalAmount = 1234 + index;
            historical.TaxInvoiceIssued = true;
            historical.TaxInvoiceNumber = string.Empty;
            var latest = NewInvoice(
                latestId,
                rootId,
                2,
                rootId,
                latest: true,
                deleted: false,
                customer.Id,
                revision: 302 + (index * 10),
                responsibleOffice: "USENET");
            latest.TotalAmount = 9876 + index;
            latest.TaxInvoiceIssued = true;
            latest.TaxInvoiceNumber = string.Empty;
            branchInvoices.AddRange([root, historical, latest]);
            branches.Add(new BranchIds(
                rootId,
                historicalId,
                latestId));
        }

        var invoices = new List<LocalInvoice>
        {
            scopeRoot,
            scopeLatest
        };
        invoices.AddRange(rerootInvoices);
        invoices.AddRange(branchInvoices);
        db.Invoices.AddRange(invoices);

        var activeInvoices = invoices
            .Where(invoice => !invoice.IsDeleted)
            .ToList();
        var lines = activeInvoices
            .Select((invoice, index) =>
                NewLine(
                    G(10001 + index),
                    invoice.Id,
                    10 + index))
            .ToList();
        db.InvoiceLines.AddRange(lines);

        var payments = new[]
        {
            NewPayment(G(11001), scopeLatest.Id),
            NewPayment(G(11002), reroots[0].ActiveId),
            NewPayment(G(11003), reroots[1].ActiveId)
        };
        db.Payments.AddRange(payments);

        db.Transactions.AddRange(
            NewTransaction(
                G(12001),
                scopeLatest.Id,
                customer.Id),
            NewTransaction(
                G(12002),
                reroots[0].ActiveId,
                customer.Id,
                reroots[0].RentalProfileId,
                reroots[0].RentalRunId),
            NewTransaction(
                G(12003),
                reroots[1].ActiveId,
                customer.Id,
                reroots[1].RentalProfileId,
                reroots[1].RentalRunId));

        var crossLines = lines
            .Where(line =>
                line.InvoiceId == scopeRoot.Id ||
                line.InvoiceId == scopeLatest.Id)
            .ToList();
        db.InventoryMovements.AddRange(
            Enumerable.Range(0, 3).Select(index =>
                new LocalInventoryMovement
                {
                    Id = G(13001 + index),
                    InvoiceId = crossLines[index % crossLines.Count].InvoiceId,
                    InvoiceLineId = crossLines[index % crossLines.Count].Id,
                    QuantityDelta = index + 1,
                    Amount = 10 + index
                }));
        db.CostAllocations.AddRange(
            Enumerable.Range(0, 3).Select(index =>
                new LocalCostAllocation
                {
                    Id = G(14001 + index),
                    SalesInvoiceId =
                        crossLines[index % crossLines.Count].InvoiceId,
                    SalesInvoiceLineId =
                        crossLines[index % crossLines.Count].Id,
                    Quantity = 1,
                    CostAmount = 10 + index
                }));

        return new FiveGroupScenario(
            scopeRootId,
            scopeLatestId,
            reroots,
            branches,
            invoices.Select(invoice => invoice.Id).ToHashSet(),
            payments.Length);
    }

    private static void AddDeterministicBranchScenario(
        LocalDbContext db,
        bool reverseInsertionOrder,
        bool reverseCreatedAtOrder)
    {
        var customer = NewCustomer(G(9500));
        db.Customers.Add(customer);
        var root = NewInvoice(
            G(501),
            G(501),
            1,
            null,
            latest: false,
            deleted: false,
            customer.Id,
            revision: 40,
            responsibleOffice: "USENET");
        var historical = NewInvoice(
            G(502),
            G(501),
            2,
            G(501),
            latest: false,
            deleted: false,
            customer.Id,
            revision: 41,
            responsibleOffice: "USENET");
        var latest = NewInvoice(
            G(503),
            G(501),
            2,
            G(501),
            latest: true,
            deleted: false,
            customer.Id,
            revision: 42,
            responsibleOffice: "USENET");
        historical.TotalAmount = 777;
        latest.TotalAmount = 777;
        historical.TaxInvoiceIssued = true;
        latest.TaxInvoiceIssued = true;
        historical.TaxInvoiceNumber = string.Empty;
        latest.TaxInvoiceNumber = string.Empty;
        var timestamps = reverseCreatedAtOrder
            ? new[]
            {
                new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc)
            }
            : new[]
            {
                new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc),
                new DateTime(2026, 7, 3, 0, 0, 0, DateTimeKind.Utc)
            };
        root.CreatedAtUtc = timestamps[0];
        historical.CreatedAtUtc = timestamps[1];
        latest.CreatedAtUtc = timestamps[2];
        var values = new[] { root, historical, latest };
        db.Invoices.AddRange(
            reverseInsertionOrder
                ? values.Reverse()
                : values);
    }

    private static async Task<string>
        CreateInvalidFileDatabaseAsync(string failureMode)
    {
        var root = Path.Combine(
            @"D:\DevCaches\georaeplan-v1-tests\isolated-invoice-canonicalizer-fail-closed",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "test.db");
        await using var fixture =
            await TestDatabase.OpenFileAsync(databasePath);

        var customer = NewCustomer(G(9800));
        fixture.Db.Customers.Add(customer);
        var rootInvoice = NewInvoice(
            G(801),
            G(801),
            1,
            null,
            latest: false,
            deleted: false,
            customer.Id,
            revision: 80,
            responsibleOffice: "USENET");
        var historical = NewInvoice(
            G(802),
            G(801),
            2,
            G(801),
            latest: false,
            deleted: false,
            customer.Id,
            revision: 81,
            responsibleOffice: "USENET");
        var latest = NewInvoice(
            G(803),
            G(801),
            2,
            G(801),
            latest: true,
            deleted: false,
            customer.Id,
            revision: 82,
            responsibleOffice: "USENET");
        fixture.Db.Invoices.AddRange(
            rootInvoice,
            historical,
            latest);

        switch (failureMode)
        {
            case "latest_missing":
                latest.IsLatestVersion = false;
                break;
            case "latest_multiple":
                historical.IsLatestVersion = true;
                break;
            case "cycle":
                rootInvoice.PreviousVersionId = latest.Id;
                break;
            case "external_predecessor":
                latest.PreviousVersionId = G(899);
                break;
            case "disconnected":
                historical.PreviousVersionId = null;
                break;
            case "revision_zero":
                rootInvoice.Revision = 0;
                break;
            case "revision_tie":
                historical.Revision = rootInvoice.Revision;
                break;
            case "branch_width":
                fixture.Db.Invoices.Add(
                    NewInvoice(
                        G(804),
                        G(801),
                        2,
                        G(801),
                        latest: false,
                        deleted: false,
                        customer.Id,
                        revision: 83,
                        responsibleOffice: "USENET"));
                break;
            case "latest_customer_deleted":
                customer.IsDeleted = true;
                break;
            case "protected_scope":
                var otherCustomer = NewCustomer(G(9801));
                fixture.Db.Customers.Add(otherCustomer);
                rootInvoice.CustomerId = otherCustomer.Id;
                rootInvoice.ResponsibleOfficeCode = "YEONSU";
                break;
            case "rental_profile_missing":
                latest.LinkedRentalBillingProfileId = G(9901);
                break;
            case "inventory_line_mismatch":
                var rootLine = NewLine(
                    G(15001),
                    rootInvoice.Id,
                    1);
                fixture.Db.InvoiceLines.Add(rootLine);
                fixture.Db.InventoryMovements.Add(
                    new LocalInventoryMovement
                    {
                        Id = G(15002),
                        InvoiceId = latest.Id,
                        InvoiceLineId = rootLine.Id,
                        QuantityDelta = 1
                    });
                break;
            case "deleted_dependency":
                fixture.Db.Invoices.RemoveRange(
                    rootInvoice,
                    historical,
                    latest);
                var deletedRoot = NewInvoice(
                    G(811),
                    G(811),
                    1,
                    null,
                    latest: false,
                    deleted: true,
                    customer.Id,
                    revision: 90,
                    responsibleOffice: "USENET");
                deletedRoot.IsDirty = false;
                var active = NewInvoice(
                    G(812),
                    G(811),
                    2,
                    G(811),
                    latest: true,
                    deleted: false,
                    customer.Id,
                    revision: 91,
                    responsibleOffice: "USENET");
                fixture.Db.Invoices.AddRange(
                    deletedRoot,
                    active);
                fixture.Db.InvoiceLines.Add(
                    NewLine(G(15003), deletedRoot.Id, 1));
                break;
            case "partial_push":
                fixture.Db.SyncOutboxEntries.Add(
                    new LocalSyncOutboxEntry
                    {
                        Id = G(16001),
                        MutationId = "partial-push",
                        EntityName = nameof(LocalInvoice),
                        EntityId = latest.Id,
                        Status = "Prepared"
                    });
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(failureMode));
        }

        fixture.Db.SyncOutboxEntries.Add(
            new LocalSyncOutboxEntry
            {
                Id = G(16002),
                MutationId = "acknowledged-baseline",
                EntityName = nameof(LocalInvoice),
                EntityId = rootInvoice.Id,
                Status = "Acknowledged",
                AcceptedRevision = 80
            });
        await fixture.Db.SaveChangesAsync();
        return databasePath;
    }

    private static async Task<CapturedState> CaptureStateAsync(
        LocalDbContext db,
        IReadOnlySet<Guid> memberIds)
    {
        var invoices = await db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => memberIds.Contains(invoice.Id))
            .ToListAsync();
        var activeIds = invoices
            .Where(invoice => !invoice.IsDeleted)
            .Select(invoice => FormatId(invoice.Id))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var latestIds = invoices
            .Where(invoice =>
                !invoice.IsDeleted &&
                invoice.IsLatestVersion)
            .Select(invoice => FormatId(invoice.Id))
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        var protectedState = invoices
            .OrderBy(invoice => FormatId(invoice.Id))
            .Select(invoice => JsonSerializer.Serialize(new
            {
                invoice.Id,
                invoice.CustomerId,
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.InvoiceNumber,
                invoice.TaxInvoiceNumber,
                invoice.TaxInvoiceIssued,
                invoice.InvoiceDate,
                invoice.TotalAmount,
                invoice.SupplyAmount,
                invoice.VatAmount,
                invoice.LinkedRentalBillingProfileId,
                invoice.LinkedRentalBillingRunId,
                invoice.IsLatestVersion,
                invoice.IsDeleted,
                invoice.CreatedAtUtc,
                invoice.UpdatedAtUtc,
                invoice.Revision,
                invoice.IsDirty
            }))
            .ToArray();
        var references = await CaptureDependencyReferencesAsync(
            db,
            memberIds);
        return new CapturedState(
            activeIds,
            latestIds,
            protectedState,
            references);
    }

    private static async Task<string>
        CaptureCanonicalizationMetadataAsync(LocalDbContext db)
    {
        var values = await db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.VersionGroupId,
                invoice.VersionNumber,
                invoice.PreviousVersionId,
                invoice.ResponsibleOfficeCode
            })
            .ToListAsync();
        return JsonSerializer.Serialize(values);
    }

    private static async Task<string>
        CaptureDeletedMetadataAsync(LocalDbContext db)
    {
        var values = await db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(invoice => invoice.IsDeleted)
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => new
            {
                invoice.VersionGroupId,
                invoice.VersionNumber,
                invoice.PreviousVersionId,
                invoice.ResponsibleOfficeCode
            })
            .ToListAsync();
        return JsonSerializer.Serialize(values);
    }

    private static async Task<string> CaptureSchemaAndSettingsAsync(
        string databasePath)
    {
        await using var fixture =
            await TestDatabase.OpenFileAsync(databasePath);
        var schema = new List<string>();
        await using (var command =
                     fixture.Connection.CreateCommand())
        {
            command.CommandText =
                "SELECT type, name, COALESCE(sql, '') FROM sqlite_master ORDER BY type, name;";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                schema.Add(
                    $"{reader.GetString(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
            }
        }
        var settings = await fixture.Db.Settings
            .AsNoTracking()
            .OrderBy(setting => setting.Key)
            .Select(setting => new
            {
                setting.Key,
                setting.Value
            })
            .ToListAsync();
        return JsonSerializer.Serialize(new
        {
            schema,
            settings
        });
    }

    private static async Task<string[]> CaptureDependencyReferencesAsync(
        LocalDbContext db,
        IReadOnlySet<Guid> memberIds)
    {
        var values = new List<string>();
        values.AddRange((await db.InvoiceLines
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(line => memberIds.Contains(line.InvoiceId))
                .ToListAsync())
            .Select(line =>
                $"InvoiceLine|{FormatId(line.Id)}|{FormatId(line.InvoiceId)}"));
        values.AddRange((await db.Payments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(payment =>
                    memberIds.Contains(payment.InvoiceId))
                .ToListAsync())
            .Select(payment =>
                $"Payment|{FormatId(payment.Id)}|{FormatId(payment.InvoiceId)}"));
        values.AddRange((await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transaction =>
                    transaction.LinkedInvoiceId.HasValue &&
                    memberIds.Contains(
                        transaction.LinkedInvoiceId.Value))
                .ToListAsync())
            .Select(transaction => string.Join(
                "|",
                "Transaction",
                FormatId(transaction.Id),
                FormatNullableId(transaction.LinkedInvoiceId))));
        values.AddRange((await db.Transactions
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(transaction =>
                    transaction.LinkedInvoiceId.HasValue &&
                    memberIds.Contains(
                        transaction.LinkedInvoiceId.Value) &&
                    (transaction.LinkedRentalBillingProfileId.HasValue ||
                     transaction.LinkedRentalBillingRunId.HasValue))
                .ToListAsync())
            .Select(transaction => string.Join(
                "|",
                "TransactionRental",
                FormatId(transaction.Id),
                FormatNullableId(
                    transaction.LinkedRentalBillingProfileId),
                FormatNullableId(
                    transaction.LinkedRentalBillingRunId))));
        values.AddRange((await db.InventoryMovements
                .AsNoTracking()
                .Where(movement =>
                    movement.InvoiceId.HasValue &&
                    memberIds.Contains(movement.InvoiceId.Value))
                .ToListAsync())
            .Select(movement => string.Join(
                "|",
                "InventoryMovement",
                FormatId(movement.Id),
                FormatNullableId(movement.InvoiceId),
                FormatNullableId(movement.InvoiceLineId))));
        values.AddRange((await db.CostAllocations
                .AsNoTracking()
                .Where(allocation =>
                    memberIds.Contains(
                        allocation.SalesInvoiceId) ||
                    (allocation.PurchaseInvoiceId.HasValue &&
                     memberIds.Contains(
                         allocation.PurchaseInvoiceId.Value)))
                .ToListAsync())
            .Select(allocation => string.Join(
                "|",
                "CostAllocation",
                FormatId(allocation.Id),
                FormatId(allocation.SalesInvoiceId),
                FormatId(allocation.SalesInvoiceLineId),
                FormatNullableId(allocation.PurchaseInvoiceId),
                FormatNullableId(
                    allocation.PurchaseInvoiceLineId))));
        values.AddRange((await db.Invoices
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Where(invoice =>
                    memberIds.Contains(invoice.Id) &&
                    (invoice.LinkedRentalBillingProfileId.HasValue ||
                     invoice.LinkedRentalBillingRunId.HasValue))
                .ToListAsync())
            .Select(invoice => string.Join(
                "|",
                "InvoiceRental",
                FormatId(invoice.Id),
                FormatNullableId(
                    invoice.LinkedRentalBillingProfileId),
                FormatNullableId(
                    invoice.LinkedRentalBillingRunId))));
        return values.OrderBy(
                value => value,
                StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<string> CaptureFailureStateAsync(
        string databasePath)
    {
        await using var fixture =
            await TestDatabase.OpenFileAsync(databasePath);
        var invoices = await fixture.Db.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .OrderBy(invoice => invoice.Id)
            .Select(invoice => new
            {
                invoice.Id,
                invoice.VersionGroupId,
                invoice.VersionNumber,
                invoice.PreviousVersionId,
                invoice.IsLatestVersion,
                invoice.ResponsibleOfficeCode,
                invoice.Revision,
                invoice.IsDirty,
                invoice.IsDeleted
            })
            .ToListAsync();
        var outbox = await fixture.Db.SyncOutboxEntries
            .AsNoTracking()
            .OrderBy(entry => entry.Id)
            .Select(entry => new
            {
                entry.Id,
                entry.EntityId,
                entry.Status,
                entry.AcceptedRevision
            })
            .ToListAsync();
        return JsonSerializer.Serialize(new
        {
            invoices,
            outbox
        });
    }

    private static LocalSyncOutboxEntry NewAuthorizedLegacyOutbox(
        string mutationId)
        => new()
        {
            Id = G(17001),
            MutationId = mutationId,
            DeviceId = "isolated-test-device",
            EntityName = nameof(LocalItem),
            EntityId = G(17002),
            ExpectedRevision = 0,
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            BusinessDatabaseName = "isolated-test",
            SessionId = G(17003),
            UserId = G(17004),
            Status = "Prepared",
            ErrorMessage = string.Empty,
            PreparedAtUtc =
                new DateTime(
                    2026,
                    8,
                    4,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc),
            SentAtUtc =
                new DateTime(
                    2026,
                    8,
                    4,
                    0,
                    1,
                    0,
                    DateTimeKind.Utc),
            AcknowledgedAtUtc =
                new DateTime(
                    2026,
                    8,
                    4,
                    0,
                    2,
                    0,
                    DateTimeKind.Utc),
            AcceptedRevision = 0,
            AcceptedUpdatedAtUtc =
                new DateTime(
                    2026,
                    8,
                    4,
                    0,
                    2,
                    0,
                    DateTimeKind.Utc)
        };

    private static LocalCustomer NewCustomer(Guid id)
        => new()
        {
            Id = id,
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            NameOriginal = $"customer-{FormatId(id)}",
            NameMatchKey = FormatId(id),
            TradeType = CustomerTradeTypes.Sales,
            IsDirty = false,
            Revision = 1
        };

    private static LocalRentalBillingProfile NewRentalProfile(
        Guid id,
        Guid customerId)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            ProfileKey = $"profile-{FormatId(id)}",
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            IsActive = true,
            IsDeleted = false,
            IsDirty = false,
            Revision = 1
        };

    private static LocalInvoice NewInvoice(
        Guid id,
        Guid groupId,
        int versionNumber,
        Guid? previousVersionId,
        bool latest,
        bool deleted,
        Guid customerId,
        long revision,
        string responsibleOffice)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = responsibleOffice,
            InvoiceNumber = $"INV-{FormatId(id)}",
            TaxInvoiceNumber = $"TAX-{FormatId(id)}",
            VoucherType = VoucherType.Sales,
            InvoiceDate = new DateOnly(2026, 6, 1),
            TotalAmount = 100,
            SupplyAmount = 90,
            VatAmount = 10,
            VersionGroupId = groupId,
            VersionNumber = versionNumber,
            PreviousVersionId = previousVersionId,
            IsLatestVersion = latest,
            IsConfirmed = true,
            IsDeleted = deleted,
            IsDirty = false,
            Revision = revision,
            CreatedAtUtc =
                new DateTime(
                    2026,
                    1,
                    1,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc).AddMinutes(revision),
            UpdatedAtUtc =
                new DateTime(
                    2026,
                    1,
                    2,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc).AddMinutes(revision),
            LastSavedAtUtc =
                new DateTime(
                    2026,
                    1,
                    2,
                    0,
                    0,
                    0,
                    DateTimeKind.Utc).AddMinutes(revision),
            ConcurrencyStamp = $"stamp-{FormatId(id)}"
        };

    private static LocalInvoiceLine NewLine(
        Guid id,
        Guid invoiceId,
        decimal amount)
        => new()
        {
            Id = id,
            InvoiceId = invoiceId,
            ItemNameOriginal = $"item-{FormatId(id)}",
            Quantity = 1,
            UnitPrice = amount,
            LineAmount = amount,
            IsDeleted = false
        };

    private static LocalPayment NewPayment(
        Guid id,
        Guid invoiceId)
        => new()
        {
            Id = id,
            InvoiceId = invoiceId,
            Amount = 10,
            IsDeleted = false,
            IsDirty = false,
            Revision = 1
        };

    private static LocalTransaction NewTransaction(
        Guid id,
        Guid invoiceId,
        Guid customerId,
        Guid? rentalProfileId = null,
        Guid? rentalRunId = null)
        => new()
        {
            Id = id,
            CustomerId = customerId,
            TenantCode = "USENET_GROUP",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            LinkedInvoiceId = invoiceId,
            LinkedRentalBillingProfileId = rentalProfileId,
            LinkedRentalBillingRunId = rentalRunId,
            SettlementAmount = 10,
            IsDeleted = false,
            IsDirty = false,
            Revision = 1
        };

    private static string ComputeFileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static string NewRecoveryRoot(string scenario)
    {
        var root = Path.Combine(
            @"D:\DevCaches\georaeplan-v1-tests\canonicalizer-recovery-security",
            scenario,
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string RecoveryPath(string root)
        => Path.Combine(
            root,
            IsolatedLegacyInvoiceSeedCanonicalizer
                .RecoveryResultFileName);

    private static void TamperAuthenticatedRecoveryArtifact(
        string path,
        string mutation)
    {
        var artifact = JsonNode.Parse(
            File.ReadAllText(path, Encoding.UTF8))!.AsObject();
        var report = JsonNode.Parse(
            artifact["reportJson"]!.GetValue<string>())!.AsObject();
        var groups = report["groups"]!.AsArray();
        var group = groups[0]!.AsObject();
        switch (mutation)
        {
            case "schema":
                report["schemaVersion"] = 3;
                break;
            case "succeeded":
                report["succeeded"] = false;
                break;
            case "scope":
                report["seedScope"] = "tampered";
                break;
            case "group-count":
                report["changedGroupCount"] = 6;
                break;
            case "invoice-count":
                report["changedInvoiceCount"] = 6;
                break;
            case "deleted-count":
                report["excludedDeletedInvoiceCount"] = 3;
                break;
            case "before-hash":
                report["beforeMetadataSha256"] =
                    DifferentSha256(
                        report["beforeMetadataSha256"]!
                            .GetValue<string>());
                break;
            case "after-hash":
                report["afterMetadataSha256"] =
                    DifferentSha256(
                        report["afterMetadataSha256"]!
                            .GetValue<string>());
                break;
            case "active-hash":
                report["activeInvoiceIdsSha256"] =
                    DifferentSha256(
                        report["activeInvoiceIdsSha256"]!
                            .GetValue<string>());
                break;
            case "latest-hash":
                report["latestInvoiceBusinessSha256"] =
                    DifferentSha256(
                        report["latestInvoiceBusinessSha256"]!
                            .GetValue<string>());
                break;
            case "dependency-hash":
                report["dependencyReferencesSha256"] =
                    DifferentSha256(
                        report["dependencyReferencesSha256"]!
                            .GetValue<string>());
                break;
            case "group-removed":
                groups.RemoveAt(groups.Count - 1);
                break;
            case "group-mode":
                group["mode"] = "tampered";
                break;
            case "group-ordinal":
                group["groupOrdinal"] = 2;
                break;
            case "group-field-set":
                group["changedMetadataFields"] =
                    new JsonArray("InvoiceNumber");
                break;
            case "group-fingerprint":
                group["groupFingerprintSha256"] =
                    DifferentSha256(
                        group["groupFingerprintSha256"]!
                            .GetValue<string>());
                break;
            case "group-before-hash":
                group["beforeMetadataSha256"] =
                    DifferentSha256(
                        group["beforeMetadataSha256"]!
                            .GetValue<string>());
                break;
            case "group-after-hash":
                group["afterMetadataSha256"] =
                    DifferentSha256(
                        group["afterMetadataSha256"]!
                            .GetValue<string>());
                break;
            case "group-active-count":
                group["activeInvoiceCount"] = 0;
                break;
            case "group-deleted-count":
                group["excludedDeletedInvoiceCount"] = 9;
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(mutation),
                    mutation,
                    null);
        }

        var reportJson = report.ToJsonString(
            new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = false
            });
        artifact["reportJson"] = reportJson;
        artifact["reportSha256"] = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(reportJson)));
        File.WriteAllText(
            path,
            artifact.ToJsonString(
                new JsonSerializerOptions
                {
                    PropertyNamingPolicy =
                        JsonNamingPolicy.CamelCase,
                    WriteIndented = false
                }),
            new UTF8Encoding(false));
    }

    private static string DifferentSha256(string value)
        => $"{(value[0] == 'F' ? 'E' : 'F')}{value[1..]}";

    private static Guid G(int value)
        => Guid.Parse(
            $"00000000-0000-0000-0000-{value:000000000000}");

    private static string FormatId(Guid value)
        => value.ToString("D").ToUpperInvariant();

    private static string FormatNullableId(Guid? value)
        => value.HasValue
            ? FormatId(value.Value)
            : "NONE";

    [DllImport(
        "kernel32.dll",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private static string Between(
        string source,
        string start,
        string end)
    {
        var startIndex = source.IndexOf(
            start,
            StringComparison.Ordinal);
        Assert.True(startIndex >= 0);
        var endIndex = source.IndexOf(
            end,
            startIndex,
            StringComparison.Ordinal);
        Assert.True(endIndex > startIndex);
        return source[startIndex..endIndex];
    }

    private static int CountOccurrences(
        string source,
        string value)
    {
        var count = 0;
        var index = 0;
        while ((index = source.IndexOf(
                   value,
                   index,
                   StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string RepositoryFile(params string[] segments)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(
                    Path.Combine(current.FullName, "거래플랜.sln")))
            {
                return Path.Combine(
                    [current.FullName, .. segments]);
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the repository root.");
    }

    private sealed record RerootIds(
        Guid DeletedRootId,
        Guid ActiveId,
        Guid RentalProfileId,
        Guid RentalRunId);

    private sealed record BranchIds(
        Guid RootId,
        Guid HistoricalSiblingId,
        Guid LatestId);

    private sealed record FiveGroupScenario(
        Guid ScopeRootId,
        Guid ScopeLatestId,
        IReadOnlyList<RerootIds> Reroots,
        IReadOnlyList<BranchIds> Branches,
        IReadOnlySet<Guid> AllMemberIds,
        int ExpectedPaymentCount);

    private sealed record CapturedState(
        string[] ActiveInvoiceIds,
        string[] LatestInvoiceIds,
        string[] ProtectedInvoiceState,
        string[] DependencyReferences);

    private sealed class InvoiceUpdateCommandInterceptor
        : DbCommandInterceptor
    {
        private int _invoiceUpdateCount;

        public int InvoiceUpdateCount => _invoiceUpdateCount;

        public override ValueTask<InterceptionResult<int>>
            NonQueryExecutingAsync(
                DbCommand command,
                CommandEventData eventData,
                InterceptionResult<int> result,
                CancellationToken cancellationToken = default)
        {
            if (command.CommandText.TrimStart().StartsWith(
                    "UPDATE \"Invoices\"",
                    StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _invoiceUpdateCount);
            }

            return base.NonQueryExecutingAsync(
                command,
                eventData,
                result,
                cancellationToken);
        }
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(string? value)
            => throw new IOException("injected output failure");
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private TestDatabase(
            SqliteConnection connection,
            LocalDbContext db)
        {
            Connection = connection;
            Db = db;
        }

        public SqliteConnection Connection { get; }

        public LocalDbContext Db { get; }

        public static async Task<TestDatabase> CreateInMemoryAsync(
            IInterceptor? interceptor = null)
        {
            var connection =
                new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = NewContext(connection, interceptor);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public static async Task<TestDatabase> OpenFileAsync(
            string databasePath)
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Pooling = false
                }.ToString());
            await connection.OpenAsync();
            var db = NewContext(connection);
            await db.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, db);
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }

        private static LocalDbContext NewContext(
            SqliteConnection connection,
            IInterceptor? interceptor = null)
        {
            var options = new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection);
            if (interceptor is not null)
                options.AddInterceptors(interceptor);
            return new LocalDbContext(options.Options);
        }
    }
}
