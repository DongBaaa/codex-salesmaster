using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Data;

// Startup-only: the caller must resolve one returned generation for all contexts
// and close those contexts before restoring. Not yet wired to the application DI.
internal sealed class LocalDatabaseGenerationStore
{
    private const int Format = 1;
    private readonly string _root;
    private string SelectorPath => Path.Combine(_root, "active.json");
    internal Action? AfterSelectorSwitchForTests { get; set; }

    internal sealed record Generation(string Id, string Root, IReadOnlyList<string> Tenants)
    {
        public CommonAuthenticationDatabase Authentication => new(Root);
        public BusinessCacheDatabase Business(string tenant)
        {
            var database = new BusinessCacheDatabase(Root, tenant);
            if (!Tenants.Contains(database.TenantCode, StringComparer.Ordinal))
                throw new InvalidOperationException("선택한 복구 묶음에 해당 업체 DB가 없습니다.");
            return database;
        }
    }

    private sealed record Manifest(int Version, string Id, string BackupSetId, string[] Tenants);
    private sealed record Selector(int Version, string Id, string ManifestSha256);
    private sealed record Package(string Path, string Sha256);
    private sealed record TenantPackage(string Tenant, string Sha256);
    private sealed record BackupSet(int Version, string Id, string AuthenticationSha256, TenantPackage[] Businesses);

    public LocalDatabaseGenerationStore(string root)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("복구 묶음 저장소에는 절대 경로가 필요합니다.", nameof(root));
        _root = Path.GetFullPath(root);
        ValidatePath(_root);
    }

    public Task<Generation?> ReadActiveAsync(CancellationToken ct = default)
        => ReadActiveCoreAsync(validateDatabases: true, ct);

    private async Task<Generation?> ReadActiveCoreAsync(bool validateDatabases, CancellationToken ct)
    {
        ValidatePath(SelectorPath);
        if (!File.Exists(SelectorPath)) return null;
        // Delete sharing permits atomic File.Replace while a reader holds the old
        // selector; each reader resolves exactly one complete immutable identity.
        var bytes = ReadSmallFile(SelectorPath);
        var selector = JsonSerializer.Deserialize<Selector>(bytes)
            ?? throw new InvalidDataException("복구 묶음 선택 정보를 읽을 수 없습니다.");
        if (selector.Version != Format || !IsId(selector.Id))
            throw new InvalidDataException("복구 묶음 선택 정보의 형식이 올바르지 않습니다.");
        var root = GenerationRoot(selector.Id);
        var manifestBytes = ReadSmallFile(Path.Combine(root, "generation.json"));
        if (Convert.ToHexString(SHA256.HashData(manifestBytes)) != selector.ManifestSha256)
            throw new InvalidDataException("복구 묶음 구성 정보가 변경되었습니다.");
        var manifest = JsonSerializer.Deserialize<Manifest>(manifestBytes)
            ?? throw new InvalidDataException("복구 묶음 구성 정보를 읽을 수 없습니다.");
        if (manifest.Version != Format || manifest.Id != selector.Id || !IsId(manifest.BackupSetId) || manifest.Tenants is null || manifest.Tenants.Length == 0)
            throw new InvalidDataException("복구 묶음 구성이 올바르지 않습니다.");
        var tenants = NormalizeTenants(root, manifest.Tenants);
        if (!tenants.SequenceEqual(manifest.Tenants, StringComparer.Ordinal))
            throw new InvalidDataException("복구 묶음의 업체 목록이 정규 형식과 다릅니다.");
        var generation = new Generation(selector.Id, root, Array.AsReadOnly(tenants));
        if (validateDatabases) await ValidateDatabasesAsync(generation, ct);
        return generation;
    }

    public static async Task CreateBackupSetFromClosedStoresAsync(string sourceRoot,
        IEnumerable<string> businesses, string destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourceRoot) || !Path.IsPathFullyQualified(destination))
            throw new ArgumentException("원본과 백업 묶음에는 절대 경로가 필요합니다.");
        ValidatePath(sourceRoot); ValidatePath(destination);
        if (Directory.Exists(destination) || File.Exists(destination))
            throw new IOException("백업 묶음 대상은 새 경로여야 합니다.");
        var tenants = NormalizeTenants(sourceRoot, businesses);
        if (tenants.Length == 0) throw new ArgumentException("업체 DB가 하나 이상 필요합니다.");
        var source = new Generation(Guid.NewGuid().ToString("N"), sourceRoot, Array.AsReadOnly(tenants));
        await ValidateDatabasesAsync(source, ct);
        var members = new[] { source.Authentication.DatabasePath }
            .Concat(tenants.Select(tenant => source.Business(tenant).DatabasePath)).ToArray();
        var leases = new List<FileStream>();
        try
        {
            // All source databases are held read-only together for the entire set.
            // Refuse live WAL/journal state instead of capturing different moments.
            foreach (var path in members)
            {
                ValidatePath(path);
                leases.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    ValidatePath(path + suffix);
                    if (File.Exists(path + suffix))
                        throw new IOException("DB 연결과 보조 파일이 정리된 뒤 백업 묶음을 생성해야 합니다.");
                }
            }
            Directory.CreateDirectory(destination);
            async Task<string> Create(string database, string name)
            {
                var output = Path.Combine(destination, name + ".gpbackup");
                await BackupService.CreateConsistentBackupPackageAsync(database,
                    Path.Combine(Path.GetDirectoryName(database)!, "attachments"), output, ct);
                using var file = File.OpenRead(output);
                return Convert.ToHexString(SHA256.HashData(file));
            }
            var commonHash = await Create(members[0], "authentication");
            var packages = new List<TenantPackage>();
            foreach (var tenant in tenants)
                packages.Add(new TenantPackage(tenant, await Create(source.Business(tenant).DatabasePath, tenant)));
            ct.ThrowIfCancellationRequested();
            WriteNewFile(Path.Combine(destination, "backup-set.json"), JsonSerializer.SerializeToUtf8Bytes(
                new BackupSet(Format, Guid.NewGuid().ToString("N"), commonHash, packages.ToArray())));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    public Task<Generation> RestoreBeforeOpeningContextsAsync(string backupSetDirectory,
        string? expectedActiveId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(backupSetDirectory))
            throw new ArgumentException("백업 묶음에는 절대 경로가 필요합니다.");
        var set = JsonSerializer.Deserialize<BackupSet>(ReadSmallFile(Path.Combine(backupSetDirectory, "backup-set.json")))
            ?? throw new InvalidDataException("완료된 백업 묶음이 아닙니다.");
        if (set.Version != Format || !IsId(set.Id) || set.Businesses is null || set.Businesses.Length == 0)
            throw new InvalidDataException("백업 묶음 형식이 올바르지 않습니다.");
        var tenants = NormalizeTenants(_root, set.Businesses.Select(entry => entry.Tenant));
        if (!tenants.SequenceEqual(set.Businesses.Select(entry => entry.Tenant), StringComparer.Ordinal))
            throw new InvalidDataException("백업 업체 목록이 정규 형식과 다릅니다.");
        return RestorePackagesBeforeOpeningContextsAsync(
            new Package(Path.Combine(backupSetDirectory, "authentication.gpbackup"), set.AuthenticationSha256),
            set.Businesses.ToDictionary(entry => entry.Tenant,
                entry => new Package(Path.Combine(backupSetDirectory, entry.Tenant + ".gpbackup"), entry.Sha256), StringComparer.Ordinal),
            set.Id, expectedActiveId, ct);
    }

    private async Task<Generation> RestorePackagesBeforeOpeningContextsAsync(
        Package authenticationPackage,
        IReadOnlyDictionary<string, Package> businessPackages,
        string backupSetId,
        string? expectedActiveId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (businessPackages.Count == 0)
            throw new ArgumentException("업체 백업이 하나 이상 필요합니다.", nameof(businessPackages));
        var tenants = NormalizeTenants(_root, businessPackages.Keys);
        var packages = businessPackages.ToDictionary(
            entry => new BusinessCacheDatabase(_root, entry.Key).TenantCode,
            entry => entry.Value, StringComparer.Ordinal);
        ValidatePath(_root);
        Directory.CreateDirectory(_root);
        var lockPath = Path.Combine(_root, "activation.lock");
        ValidatePath(lockPath);
        using var lease = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        // A damaged current DB must not prevent recovery from a sound set.
        // Validate the old identity/membership, but do not require its DBs to open.
        var active = await ReadActiveCoreAsync(validateDatabases: false, ct);
        if (!string.Equals(active?.Id, expectedActiveId, StringComparison.Ordinal))
            throw new InvalidOperationException("현재 복구 묶음이 변경되어 작업을 중단했습니다.");
        if (active is not null && active.Tenants.Except(tenants, StringComparer.Ordinal).Any())
            throw new InvalidOperationException("현재 업체 DB가 빠진 백업 묶음은 자동 적용할 수 없습니다.");

        var previousSelector = File.Exists(SelectorPath) ? ReadSmallFile(SelectorPath) : null;
        var id = Guid.NewGuid().ToString("N");
        var generation = new Generation(id, GenerationRoot(id), Array.AsReadOnly(tenants));
        if (Directory.Exists(generation.Root) || File.Exists(generation.Root))
            throw new IOException("새 복구 묶음 경로가 이미 존재합니다.");
        Directory.CreateDirectory(generation.Root);
        // Failure leaves an unselected generation for inspection. Neither old
        // generations nor caller-owned packages are overwritten or removed.
        RestoreMember(authenticationPackage, generation.Authentication.DatabasePath, ct);
        foreach (var tenant in tenants)
            RestoreMember(packages[tenant], generation.Business(tenant).DatabasePath, ct);
        await ValidateDatabasesAsync(generation, ct);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(new Manifest(Format, id, backupSetId, tenants));
        WriteNewFile(Path.Combine(generation.Root, "generation.json"), manifestBytes);
        var selector = JsonSerializer.SerializeToUtf8Bytes(new Selector(Format, id,
            Convert.ToHexString(SHA256.HashData(manifestBytes))));
        ct.ThrowIfCancellationRequested();
        ReplaceSelector(selector);
        try
        {
            AfterSelectorSwitchForTests?.Invoke();
            var verified = await ReadActiveAsync(CancellationToken.None);
            if (verified?.Id != id)
                throw new InvalidDataException("복구 묶음 교체 후 검증이 일치하지 않습니다.");
            return verified;
        }
        catch
        {
            // Keep the same exclusive writer lease across rollback. A rollback
            // failure surfaces as an error; callers must not open any contexts.
            if (!ReadSmallFile(SelectorPath).SequenceEqual(selector))
                throw new IOException("복구 묶음 선택 정보가 외부에서 변경되어 자동 원복을 중단했습니다.");
            if (previousSelector is null) File.Delete(SelectorPath);
            else ReplaceSelector(previousSelector);
            throw;
        }
    }

    private static string[] NormalizeTenants(string root, IEnumerable<string> values)
    {
        var tenants = values.Select(value => new BusinessCacheDatabase(root, value).TenantCode).ToArray();
        if (tenants.Length != tenants.Distinct(StringComparer.Ordinal).Count())
            throw new ArgumentException("같은 업체를 가리키는 중복 백업이 있습니다.");
        return tenants.OrderBy(value => value, StringComparer.Ordinal).ToArray();
    }

    private static async Task ValidateDatabasesAsync(Generation generation, CancellationToken ct)
    {
        await using (await generation.Authentication.OpenAsync(ct)) { }
        foreach (var tenant in generation.Tenants)
            await using (await generation.Business(tenant).OpenAsync(ct)) { }
    }

    private static void RestoreMember(Package package, string databasePath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(package.Path) || Path.GetExtension(package.Path) != ".gpbackup")
            throw new ArgumentException("검증된 패키지의 절대 경로가 필요합니다.", nameof(package));
        ValidatePath(package.Path);
        // Hold the input against concurrent write/delete for the complete restore.
        using var inputLease = new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (Convert.ToHexString(SHA256.HashData(inputLease)) != package.Sha256)
            throw new InvalidDataException("백업 묶음의 파일 해시가 일치하지 않습니다.");
        var memberRoot = Path.GetDirectoryName(databasePath)!;
        BackupService.RestoreBackupArtifact(package.Path, databasePath,
            Path.Combine(memberRoot, "attachments"), Path.Combine(memberRoot, "restore-evidence"));
        ct.ThrowIfCancellationRequested();
    }

    private string GenerationRoot(string id)
    {
        if (!IsId(id)) throw new InvalidDataException("복구 묶음 ID가 올바르지 않습니다.");
        var path = Path.Combine(_root, "generations", id);
        ValidatePath(path);
        return path;
    }

    private static bool IsId(string id)
        => Guid.TryParseExact(id, "N", out var value) && value.ToString("N") == id;

    private static byte[] ReadSmallFile(string path)
    {
        ValidatePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > 65536)
            throw new InvalidDataException("복구 묶음 메타데이터 크기가 올바르지 않습니다.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private void ReplaceSelector(byte[] bytes)
    {
        ValidatePath(SelectorPath);
        var temporary = Path.Combine(_root, "selector-" + Guid.NewGuid().ToString("N") + ".tmp");
        WriteNewFile(temporary, bytes);
        if (File.Exists(SelectorPath)) File.Replace(temporary, SelectorPath, null);
        else File.Move(temporary, SelectorPath);
    }

    private static void WriteNewFile(string path, byte[] bytes)
    {
        ValidatePath(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ValidatePath(string path)
        => AppPaths.EnsureNoExistingReparsePointInPathChain(path, "LocalDatabaseGenerationStore");
}
