using System.IO;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LocalDatabaseGenerationStoreTests
{
    [Fact]
    public async Task CompleteSet_RestoresOneGenerationAndLeavesPreviousFilesUnchanged()
    {
        using var fixture = new Fixture();
        var firstSet = await fixture.CreateSet("first");
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot);
        var first = await store.RestoreBeforeOpeningContextsAsync(firstSet, null);
        await Verify(first, "first");
        var firstHashes = HashFiles(first.Root);
        var secondSet = await fixture.CreateSet("second");
        var second = await store.RestoreBeforeOpeningContextsAsync(secondSet, first.Id);
        Assert.NotEqual(first.Id, second.Id);
        await Verify((await new LocalDatabaseGenerationStore(fixture.StoreRoot).ReadActiveAsync())!, "second");
        await Verify(first, "first"); // Previously resolved contexts retain one generation.
        Assert.Equal(firstHashes, HashFiles(first.Root));
        Assert.Throws<InvalidOperationException>(() => second.Business("ORG_UNSELECTED"));
        Assert.Equal(second.Business("USENET").DatabasePath, second.Business("YEONSU").DatabasePath);
    }

    [Theory]
    [InlineData("corrupt-member")]
    [InlineData("missing-member")]
    [InlineData("wrong-owner")]
    [InlineData("after-selector")]
    public async Task FailedRestore_PreservesEntirePreviousGeneration(string fault)
    {
        using var fixture = new Fixture();
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot);
        var first = await store.RestoreBeforeOpeningContextsAsync(await fixture.CreateSet("first"), null);
        var previous = HashFiles(first.Root);
        var set = await fixture.CreateSet("next");
        var member = Path.Combine(set, "USENET_GROUP.gpbackup");
        if (fault == "corrupt-member") await File.AppendAllTextAsync(member, "changed");
        if (fault == "missing-member") File.Delete(member);
        if (fault == "wrong-owner")
        {
            File.Copy(Path.Combine(set, "ITWORLD.gpbackup"), member, true);
            var manifestPath = Path.Combine(set, "backup-set.json");
            var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!;
            foreach (var row in manifest["Businesses"]!.AsArray())
                if (row!["Tenant"]!.GetValue<string>() == "USENET_GROUP")
                    row["Sha256"] = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(member)));
            await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString());
        }
        if (fault == "after-selector") store.AfterSelectorSwitchForTests = () => throw new IOException("injected after selector");
        await Assert.ThrowsAnyAsync<Exception>(() => store.RestoreBeforeOpeningContextsAsync(set, first.Id));
        var reopened = await new LocalDatabaseGenerationStore(fixture.StoreRoot).ReadActiveAsync();
        Assert.Equal(first.Id, reopened!.Id);
        await Verify(reopened, "first");
        Assert.Equal(previous, HashFiles(first.Root));
    }

    [Fact]
    public async Task WriterLockStaleSelectionAndTamperedManifestFailClosed()
    {
        using var fixture = new Fixture();
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot);
        var set = await fixture.CreateSet("first");
        var first = await store.RestoreBeforeOpeningContextsAsync(set, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreBeforeOpeningContextsAsync(set, null));
        using (new FileStream(Path.Combine(fixture.StoreRoot, "activation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<IOException>(() => store.RestoreBeforeOpeningContextsAsync(set, first.Id));
        await File.AppendAllTextAsync(Path.Combine(first.Root, "generation.json"), " ");
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReadActiveAsync());
    }

    [Fact]
    public async Task IncompleteSetAndDuplicateTenantAliasesCannotActivate()
    {
        using var fixture = new Fixture();
        var set = await fixture.CreateSet("first");
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot);
        await Assert.ThrowsAsync<ArgumentException>(() => LocalDatabaseGenerationStore.CreateBackupSetFromClosedStoresAsync(
            fixture.Source("first"), ["USENET", "YEONSU"], Path.Combine(fixture.Root, "duplicate")));
        File.Delete(Path.Combine(set, "backup-set.json"));
        await Assert.ThrowsAnyAsync<IOException>(() => store.RestoreBeforeOpeningContextsAsync(set, null));
        Assert.Null(await store.ReadActiveAsync());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.RestoreBeforeOpeningContextsAsync(set, null, new CancellationToken(true)));
    }

    [Fact]
    public async Task DamagedCurrentDatabaseCanRecoverWithoutDeletingTheDamagedEvidence()
    {
        using var fixture = new Fixture();
        var set = await fixture.CreateSet("first");
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot);
        var first = await store.RestoreBeforeOpeningContextsAsync(set, null);
        var damaged = first.Business("USENET").DatabasePath;
        await File.WriteAllTextAsync(damaged, "simulated damaged database");
        await Assert.ThrowsAnyAsync<Exception>(() => store.ReadActiveAsync());
        var replacement = await store.RestoreBeforeOpeningContextsAsync(set, first.Id);
        await Verify(replacement, "first");
        Assert.Equal("simulated damaged database", await File.ReadAllTextAsync(damaged));
    }

    [Fact]
    public async Task MissingCurrentTenantAndOpenSourceCannotProduceAnAcceptedSet()
    {
        using var fixture = new Fixture();
        var set = await fixture.CreateSet("first");
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot);
        var first = await store.RestoreBeforeOpeningContextsAsync(set, null);
        var partial = Path.Combine(fixture.Root, "partial");
        await LocalDatabaseGenerationStore.CreateBackupSetFromClosedStoresAsync(fixture.Source("first"), ["USENET"], partial);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.RestoreBeforeOpeningContextsAsync(partial, first.Id));
        Assert.Equal(first.Id, (await store.ReadActiveAsync())!.Id);
        await using var open = await new BusinessCacheDatabase(fixture.Source("first"), "USENET").OpenAsync();
        var busyBackup = Path.Combine(fixture.Root, "busy-backup");
        await Assert.ThrowsAnyAsync<Exception>(() => LocalDatabaseGenerationStore.CreateBackupSetFromClosedStoresAsync(
            fixture.Source("first"), ["USENET", "ITWORLD"], busyBackup));
        Assert.False(File.Exists(Path.Combine(busyBackup, "backup-set.json")));
    }

    [Fact]
    public async Task FirstActivationFailureLeavesNoActiveGeneration()
    {
        using var fixture = new Fixture();
        var store = new LocalDatabaseGenerationStore(fixture.StoreRoot)
        { AfterSelectorSwitchForTests = () => throw new IOException("injected first activation") };
        var set = await fixture.CreateSet("first");
        await Assert.ThrowsAsync<IOException>(() => store.RestoreBeforeOpeningContextsAsync(set, null));
        Assert.Null(await new LocalDatabaseGenerationStore(fixture.StoreRoot).ReadActiveAsync());
    }

    private static async Task Verify(LocalDatabaseGenerationStore.Generation generation, string label)
    {
        await using (var common = await generation.Authentication.OpenAsync())
        {
            Assert.Equal(label, (await common.Settings.SingleAsync(row => row.Key == "Login.SavedUsername")).Value);
            Assert.False(await common.Settings.AnyAsync(row => row.Key.StartsWith("CachedSession.")));
        }
        foreach (var tenant in new[] { "USENET", "ITWORLD" })
        {
            await using var db = await generation.Business(tenant).OpenAsync();
            var item = await db.Items.SingleAsync();
            Assert.Equal(label + tenant, item.NameOriginal);
            Assert.True(item.IsDirty);
            Assert.Equal("Prepared", (await db.SyncOutboxEntries.SingleAsync()).Status);
            var attachment = await db.TransactionAttachments.SingleAsync();
            Assert.Equal(label + " attachment " + tenant, await File.ReadAllTextAsync(attachment.StoredPath));
            Assert.StartsWith(generation.Root + Path.DirectorySeparatorChar, attachment.StoredPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string[] HashFiles(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" +
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private sealed class Fixture : IDisposable
    {
        private readonly string? _oldAppRoot = Environment.GetEnvironmentVariable("GEORAEPLAN_APP_ROOT");
        private readonly string _parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Codex", "generation-tests");
        public string Root { get; }
        public string StoreRoot => Path.Combine(Root, "store");
        public string Source(string label) => Path.Combine(Root, label + "-source");
        public Fixture()
        {
            Root = Path.Combine(_parent, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", Path.Combine(Root, "app"));
        }
        public async Task<string> CreateSet(string label)
        {
            var root = Source(label);
            var common = new CommonAuthenticationDatabase(root);
            await common.CreateNewAsync();
            await using (var db = await common.OpenAsync())
            {
                db.Settings.AddRange(new LocalSetting { Key = "Login.SavedUsername", Value = label },
                    new LocalSetting { Key = "CachedSession.fixture.Username", Value = "old authentication" });
                await db.SaveChangesAsync();
            }
            var sameId = Guid.NewGuid();
            foreach (var tenant in new[] { "USENET", "ITWORLD" })
            {
                var factory = new BusinessCacheDatabase(root, tenant);
                await factory.CreateNewAsync();
                await using var db = await factory.OpenAsync();
                db.Items.Add(new LocalItem { Id = sameId, NameOriginal = label + tenant, IsDirty = true });
                db.SyncOutboxEntries.Add(new LocalSyncOutboxEntry { Id = Guid.NewGuid(), EntityId = sameId,
                    EntityName = "LocalItem", MutationId = label + tenant, Status = "Prepared",
                    TenantCode = factory.TenantCode, OfficeCode = tenant, BusinessDatabaseName = tenant });
                var customerId = Guid.NewGuid();
                var transactionId = Guid.NewGuid();
                db.Customers.Add(new LocalCustomer { Id = customerId, NameOriginal = label,
                    NameMatchKey = label + tenant, TenantCode = factory.TenantCode,
                    OfficeCode = tenant, ResponsibleOfficeCode = tenant });
                db.Transactions.Add(new LocalTransaction { Id = transactionId, CustomerId = customerId,
                    TenantCode = factory.TenantCode, OfficeCode = tenant, ResponsibleOfficeCode = tenant });
                var attachmentRoot = Path.Combine(Path.GetDirectoryName(factory.DatabasePath)!, "attachments");
                Directory.CreateDirectory(attachmentRoot);
                var attachmentPath = Path.Combine(attachmentRoot, "proof.txt");
                await File.WriteAllTextAsync(attachmentPath, label + " attachment " + tenant);
                var bytes = await File.ReadAllBytesAsync(attachmentPath);
                db.TransactionAttachments.Add(new LocalTransactionAttachment { Id = Guid.NewGuid(),
                    TransactionId = transactionId, FileName = "proof.txt", StoredFileName = "proof.txt", StoredPath = attachmentPath,
                    FileSize = bytes.Length, FileHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
                await db.SaveChangesAsync();
            }
            var before = HashFiles(root);
            var output = Path.Combine(Root, label + "-backup");
            await LocalDatabaseGenerationStore.CreateBackupSetFromClosedStoresAsync(root, ["USENET", "ITWORLD"], output);
            Assert.Equal(before, HashFiles(root));
            return output;
        }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", _oldAppRoot);
            var full = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(full) != Path.GetFullPath(_parent) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
                throw new InvalidOperationException("Fixture cleanup escaped its owned root.");
            if (Directory.EnumerateFileSystemEntries(full, "*", SearchOption.AllDirectories)
                .Any(path => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0))
                throw new IOException("Fixture cleanup refuses reparse entries.");
            Directory.Delete(full, recursive: true);
        }
    }
}
