using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class CustomerContractCacheStore
{
    private const string AtomicOwnerSchema = "georaeplan-cache-owner-v1";
    private const string CacheScopeSchema = "georaeplan-cache-scope-v2";
    private const string PurgeWatermarkSchema =
        "georaeplan-cache-purge-watermarks-v1";
    private const string ManifestLeaseSchema =
        "georaeplan-cache-manifest-lease-v1";
    private static readonly ConcurrentDictionary<string, RefCountedManifestLock>
        ManifestWriteLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly SessionStore? _sessionStore;
    private readonly string? _rootDirectoryOverride;
    private readonly string? _appDataDirectoryOverride;
    private readonly Func<string, CancellationToken, Task>?
        _beforeAtomicPublishAsync;
    private readonly Func<string, CancellationToken, Task>?
        _beforePdfWriteAsync;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public CustomerContractCacheStore(SessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    internal CustomerContractCacheStore(string rootDirectory)
        : this(rootDirectory, beforeAtomicPublishAsync: null)
    {
    }

    internal CustomerContractCacheStore(
        string rootDirectory,
        Func<string, CancellationToken, Task>? beforeAtomicPublishAsync)
        : this(
            rootDirectory,
            beforeAtomicPublishAsync,
            beforePdfWriteAsync: null)
    {
    }

    internal CustomerContractCacheStore(
        string rootDirectory,
        Func<string, CancellationToken, Task>? beforeAtomicPublishAsync,
        Func<string, CancellationToken, Task>? beforePdfWriteAsync)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
            throw new ArgumentException("Cache root is required.", nameof(rootDirectory));

        _rootDirectoryOverride = Path.GetFullPath(rootDirectory);
        _beforeAtomicPublishAsync = beforeAtomicPublishAsync;
        _beforePdfWriteAsync = beforePdfWriteAsync;
    }

    internal CustomerContractCacheStore(
        SessionStore sessionStore,
        string appDataDirectory,
        Func<string, CancellationToken, Task>? beforeAtomicPublishAsync,
        Func<string, CancellationToken, Task>? beforePdfWriteAsync = null)
    {
        _sessionStore = sessionStore ??
            throw new ArgumentNullException(nameof(sessionStore));
        if (string.IsNullOrWhiteSpace(appDataDirectory))
        {
            throw new ArgumentException(
                "App data root is required.",
                nameof(appDataDirectory));
        }

        _appDataDirectoryOverride = Path.GetFullPath(appDataDirectory);
        _beforeAtomicPublishAsync = beforeAtomicPublishAsync;
        _beforePdfWriteAsync = beforePdfWriteAsync;
    }

    internal static int InProcessManifestLockCount =>
        ManifestWriteLocks.Count;

    internal static int CountInProcessManifestLocksForRoot(
        string rootDirectory)
    {
        var normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
        var rootPrefix =
            $"{normalizedRoot}{Path.DirectorySeparatorChar}";
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return ManifestWriteLocks.Keys.Count(path =>
            path.StartsWith(rootPrefix, comparison));
    }

    public CacheOwnerSession CaptureOwnerSession()
    {
        if (_rootDirectoryOverride is not null)
        {
            return CacheOwnerSession.ForExplicitRoot(
                _rootDirectoryOverride);
        }

        var snapshot = _sessionStore!.GetSnapshot();
        var tenantCode = string.IsNullOrWhiteSpace(snapshot.TenantCode)
            ? TenantScopeCatalog.UsenetGroup
            : snapshot.TenantCode.Trim();
        var officeCode = string.IsNullOrWhiteSpace(snapshot.OfficeCode)
            ? OfficeCodeCatalog.Usenet
            : snapshot.OfficeCode.Trim();
        var username = string.IsNullOrWhiteSpace(snapshot.Username)
            ? "anonymous"
            : snapshot.Username.Trim();
        var ownerHash = ComputeOwnerHash(
            tenantCode,
            officeCode,
            username);
        var sessionGeneration =
            string.IsNullOrWhiteSpace(snapshot.SessionGeneration)
                ? $"legacy-{ownerHash}"
                : snapshot.SessionGeneration.Trim();
        var cacheBaseDirectory = Path.GetFullPath(Path.Combine(
            _appDataDirectoryOverride ?? FileSystem.AppDataDirectory,
            "contract-cache"));
        return new CacheOwnerSession(
            tenantCode,
            officeCode,
            username,
            ownerHash,
            sessionGeneration,
            Path.Combine(
                cacheBaseDirectory,
                $"owner-{ownerHash}"),
            isExplicitRoot: false);
    }

    public bool IsOwnerSessionCurrent(CacheOwnerSession ownerSession)
    {
        ArgumentNullException.ThrowIfNull(ownerSession);
        if (ownerSession.IsExplicitRoot)
        {
            return _rootDirectoryOverride is not null &&
                   string.Equals(
                       Path.GetFullPath(_rootDirectoryOverride),
                       Path.GetFullPath(ownerSession.RootDirectory),
                       StringComparison.OrdinalIgnoreCase);
        }

        if (_sessionStore is null)
            return false;

        var current = CaptureOwnerSession();
        return current.HasSameOwnerAndSession(ownerSession);
    }

    public void ThrowIfOwnerSessionStale(
        CacheOwnerSession ownerSession)
    {
        if (!IsOwnerSessionCurrent(ownerSession))
        {
            throw new StaleCacheOwnerSessionException(
                "The authenticated mobile owner/session changed before the cache operation could commit.");
        }
    }

    private async Task<IDisposable> AcquireOwnerCommitLeaseAsync(
        CacheOwnerSession ownerSession,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ownerSession);
        if (ownerSession.IsExplicitRoot)
            return NoopDisposable.Instance;
        if (_sessionStore is null)
        {
            throw new StaleCacheOwnerSessionException(
                "The cache owner has no authenticated session store.");
        }

        var mobileOwner = _sessionStore.CaptureOwner();
        if (!ownerSession.HasSameOwnerAndSession(mobileOwner))
        {
            throw new StaleCacheOwnerSessionException(
                "The authenticated mobile owner/session changed before the cache commit lease was acquired.");
        }

        try
        {
            return await _sessionStore.AcquireOwnerCommitLeaseAsync(
                mobileOwner,
                ct);
        }
        catch (StaleMobileSessionOwnerException ex)
        {
            throw new StaleCacheOwnerSessionException(
                $"The cache owner/session changed before commit: {ex.Message}");
        }
    }

    private string ResolveRootDirectory(
        CacheOwnerSession ownerSession,
        bool requireCurrentSession)
    {
        ArgumentNullException.ThrowIfNull(ownerSession);
        if (requireCurrentSession)
            ThrowIfOwnerSessionStale(ownerSession);

        var rootDirectory = Path.GetFullPath(
            ownerSession.RootDirectory);
        if (ownerSession.IsExplicitRoot)
        {
            if (_rootDirectoryOverride is null ||
                !string.Equals(
                    rootDirectory,
                    Path.GetFullPath(_rootDirectoryOverride),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Explicit cache root does not match the captured owner session.");
            }

            Directory.CreateDirectory(rootDirectory);
            RejectReparsePoint(rootDirectory);
            return rootDirectory;
        }

        var cacheBaseDirectory = Path.GetDirectoryName(rootDirectory)
            ?? throw new InvalidOperationException(
                $"Owner cache root has no parent: {rootDirectory}");
        Directory.CreateDirectory(cacheBaseDirectory);
        RejectReparsePoint(cacheBaseDirectory);
        Directory.CreateDirectory(rootDirectory);
        RejectReparsePoint(rootDirectory);
        EnsureExactOwnerManifest(
            rootDirectory,
            ownerSession);
        return rootDirectory;
    }

    private static string ComputeOwnerHash(
        string tenantCode,
        string officeCode,
        string username)
    {
        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);
        AppendLengthDelimited(hash, tenantCode);
        AppendLengthDelimited(hash, officeCode);
        AppendLengthDelimited(hash, username);
        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void AppendLengthDelimited(
        IncrementalHash hash,
        string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(
            length,
            bytes.Length);
        hash.AppendData(length);
        hash.AppendData(bytes);
    }

    private void EnsureExactOwnerManifest(
        string rootDirectory,
        CacheOwnerSession ownerSession)
    {
        var manifestPath = Path.Combine(
            rootDirectory,
            ".owner.json");
        if (!File.Exists(manifestPath))
        {
            var temporaryPath = Path.Combine(
                rootDirectory,
                $".owner.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var stream = new FileStream(
                           temporaryPath,
                           FileMode.CreateNew,
                           FileAccess.Write,
                           FileShare.None,
                           4 * 1024,
                           FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(
                        stream,
                        new CacheScopeManifest
                        {
                            Schema = CacheScopeSchema,
                            OwnerHash = ownerSession.OwnerHash,
                            TenantCode = ownerSession.TenantCode,
                            OfficeCode = ownerSession.OfficeCode,
                            Username = ownerSession.Username
                        },
                        _jsonOptions);
                    stream.Flush(flushToDisk: true);
                }

                try
                {
                    File.Move(
                        temporaryPath,
                        manifestPath);
                }
                catch (IOException) when (File.Exists(manifestPath))
                {
                    DeleteFileRequired(temporaryPath);
                }
            }
            finally
            {
                DeleteFileRequired(temporaryPath);
            }
        }

        RejectReparsePoint(manifestPath);
        CacheScopeManifest manifest;
        try
        {
            using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4 * 1024,
                useAsync: false);
            manifest = ReadCacheScopeManifestStrict(
                stream,
                manifestPath);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Cache owner manifest is corrupt: {manifestPath}",
                ex);
        }

        if (!string.Equals(
                manifest.Schema,
                CacheScopeSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.OwnerHash,
                ownerSession.OwnerHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.TenantCode,
                ownerSession.TenantCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.OfficeCode,
                ownerSession.OfficeCode,
                StringComparison.Ordinal) ||
            !string.Equals(
                manifest.Username,
                ownerSession.Username,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Cache owner manifest does not match the exact authenticated owner: {manifestPath}");
        }
    }

    private static CacheScopeManifest ReadCacheScopeManifestStrict(
        Stream stream,
        string path)
    {
        using var document = JsonDocument.Parse(
            stream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        if (document.RootElement.ValueKind !=
            JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Cache owner manifest root must be an object: {path}");
        }

        var values = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var allowed = new HashSet<string>(
            [
                "schema",
                "ownerHash",
                "tenantCode",
                "officeCode",
                "username"
            ],
            StringComparer.Ordinal);
        foreach (var property in
                 document.RootElement.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) ||
                values.ContainsKey(property.Name) ||
                property.Value.ValueKind !=
                JsonValueKind.String)
            {
                throw new InvalidDataException(
                    $"Cache owner manifest has an invalid property '{property.Name}': {path}");
            }

            values[property.Name] =
                property.Value.GetString()
                ?? throw new InvalidDataException(
                    $"Cache owner manifest property '{property.Name}' is null: {path}");
        }

        if (values.Count != allowed.Count ||
            allowed.Any(name => !values.ContainsKey(name)))
        {
            throw new InvalidDataException(
                $"Cache owner manifest schema is incomplete: {path}");
        }

        return new CacheScopeManifest
        {
            Schema = values["schema"],
            OwnerHash = values["ownerHash"],
            TenantCode = values["tenantCode"],
            OfficeCode = values["officeCode"],
            Username = values["username"]
        };
    }

    public Task SaveCustomersAsync(
        IReadOnlyList<CustomerDto> customers,
        CancellationToken ct = default)
        => SaveCustomersAsync(
            CaptureOwnerSession(),
            customers,
            ct);

    public async Task SaveCustomersAsync(
        CacheOwnerSession ownerSession,
        IReadOnlyList<CustomerDto> customers,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        var manifestPath = GetCustomersManifestPath(rootDirectory);
        var watermarkPath = GetPurgeWatermarkPath(rootDirectory);
        await WithManifestWriteLocksAsync(
            [manifestPath, watermarkPath],
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                var currentById = File.Exists(manifestPath)
                    ? (await ReadRequiredJsonManifestAsync<
                            List<CustomerDto>>(
                            manifestPath,
                            ct))
                        .Where(customer =>
                            customer.Id != Guid.Empty)
                        .GroupBy(customer => customer.Id)
                        .ToDictionary(
                            group => group.Key,
                            group => group
                                .OrderByDescending(
                                    customer =>
                                        customer.Revision)
                                .ThenByDescending(
                                    customer =>
                                        customer.UpdatedAtUtc)
                                .First())
                    : new Dictionary<Guid, CustomerDto>();
                var filtered = customers
                    .Where(customer =>
                        customer.Id != Guid.Empty &&
                        (!watermarks.CustomerRevisions.TryGetValue(
                             customer.Id,
                             out var purgedRevision) ||
                         IsCustomerNewerThanPurge(
                             customer,
                             purgedRevision)))
                    .Select(customer =>
                        currentById.TryGetValue(
                            customer.Id,
                            out var current) &&
                        IsStrictlyNewer(
                            current,
                            customer)
                            ? CloneCustomer(current)
                            : CloneCustomer(customer))
                    .ToList();
                var responseIds = filtered
                    .Select(customer => customer.Id)
                    .ToHashSet();
                filtered.AddRange(currentById.Values
                    .Where(customer =>
                        !responseIds.Contains(customer.Id) &&
                        (!watermarks.CustomerRevisions.TryGetValue(
                             customer.Id,
                             out var purgedRevision) ||
                         IsCustomerNewerThanPurge(
                             customer,
                             purgedRevision)))
                    .Select(CloneCustomer));
                ThrowIfOwnerSessionStale(ownerSession);
                await WriteJsonAtomicallyUnderLockAsync(
                    manifestPath,
                    filtered,
                    ct,
                    () => ThrowIfOwnerSessionStale(
                        ownerSession));
            },
            ct);
    }

    public Task<IReadOnlyList<CustomerDto>> LoadCustomersAsync(
        CancellationToken ct = default)
        => LoadCustomersAsync(CaptureOwnerSession(), ct);

    public async Task<IReadOnlyList<CustomerDto>> LoadCustomersAsync(
        CacheOwnerSession ownerSession,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        var manifestPath = GetCustomersManifestPath(rootDirectory);
        var watermarkPath = GetPurgeWatermarkPath(rootDirectory);
        return await WithManifestWriteLocksAsync(
            [manifestPath, watermarkPath],
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                if (!File.Exists(manifestPath))
                    return (IReadOnlyList<CustomerDto>)
                        Array.Empty<CustomerDto>();

                var customers =
                    await ReadRequiredJsonManifestAsync<List<CustomerDto>>(
                        manifestPath,
                        ct);
                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                ThrowIfOwnerSessionStale(ownerSession);
                return customers
                    .Where(customer =>
                        !watermarks.CustomerRevisions.TryGetValue(
                            customer.Id,
                            out var purgedRevision) ||
                        IsCustomerNewerThanPurge(
                            customer,
                            purgedRevision))
                    .Select(CloneCustomer)
                    .ToList();
            },
            ct);
    }

    public Task RemoveCustomerFromIndexAsync(
        Guid customerId,
        CancellationToken ct = default)
        => RemoveCustomerFromIndexAsync(
            CaptureOwnerSession(),
            customerId,
            ct);

    public async Task RemoveCustomerFromIndexAsync(
        CacheOwnerSession ownerSession,
        Guid customerId,
        CancellationToken ct = default)
    {
        if (customerId == Guid.Empty)
            return;

        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        var manifestPath = GetCustomersManifestPath(rootDirectory);
        await WithManifestWriteLockAsync(
            manifestPath,
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                if (!File.Exists(manifestPath))
                    return;

                var customers =
                    await ReadRequiredJsonManifestAsync<List<CustomerDto>>(
                        manifestPath,
                        ct);
                if (customers.RemoveAll(customer =>
                        customer.Id == customerId) == 0)
                {
                    return;
                }

                ThrowIfOwnerSessionStale(ownerSession);
                await WriteJsonAtomicallyUnderLockAsync(
                    manifestPath,
                    customers.Select(CloneCustomer).ToList(),
                    ct,
                    () => ThrowIfOwnerSessionStale(
                        ownerSession));
            },
            ct);
    }

    public Task SaveContractsAsync(
        Guid customerId,
        IReadOnlyList<CustomerContractDto> contracts,
        CancellationToken ct = default)
        => SaveContractsAsync(
            CaptureOwnerSession(),
            customerId,
            contracts,
            ct);

    public async Task SaveContractsAsync(
        CacheOwnerSession ownerSession,
        Guid customerId,
        IReadOnlyList<CustomerContractDto> contracts,
        CancellationToken ct = default)
    {
        if (customerId == Guid.Empty)
            throw new ArgumentException(
                "Customer id is required.",
                nameof(customerId));
        ArgumentNullException.ThrowIfNull(contracts);
        if (contracts.Any(contract =>
                contract is null ||
                contract.Id == Guid.Empty ||
                contract.CustomerId != customerId) ||
            contracts
                .GroupBy(contract => contract.Id)
                .Any(group => group.Count() != 1))
        {
            throw new InvalidDataException(
                "Contract cache response contains an invalid or duplicate customer binding.");
        }

        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);

        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        var manifestPath = GetManifestPath(
            rootDirectory,
            customerId);
        var watermarkPath = GetPurgeWatermarkPath(rootDirectory);
        await WithManifestWriteLocksAsync(
            [manifestPath, watermarkPath],
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                var customerDirectory = GetCustomerDirectory(
                    rootDirectory,
                    customerId);
                Directory.CreateDirectory(customerDirectory);
                RejectReparsePoint(customerDirectory);

                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                watermarks.CustomerRevisions.TryGetValue(
                    customerId,
                    out var customerPurgeRevision);
                var currentManifest = File.Exists(manifestPath)
                    ? await ReadAndValidateContractManifestAsync(
                        manifestPath,
                        ownerSession,
                        customerId,
                        ct)
                    : null;
                var manifest = new CustomerContractCacheManifest
                {
                    Schema = CacheScopeSchema,
                    OwnerHash = ownerSession.OwnerHash,
                    CustomerId = customerId,
                    GenerationId = Guid.NewGuid(),
                    SavedAtUtc = DateTime.UtcNow,
                    Contracts = new List<CustomerContractDto>(),
                    PdfBindings = new List<CachedPdfBinding>()
                };

                foreach (var responseContract in contracts)
                {
                    if (responseContract.Id == Guid.Empty ||
                        responseContract.CustomerId != customerId)
                    {
                        throw new InvalidDataException(
                            $"Contract cache response contains an invalid customer binding: {responseContract.Id}");
                    }

                    var contractPurgeRevision = watermarks
                        .ContractRevisions.TryGetValue(
                            responseContract.Id,
                            out var specificPurgeRevision)
                        ? specificPurgeRevision
                        : 0;
                    var effectivePurgeRevision = Math.Max(
                        customerPurgeRevision,
                        contractPurgeRevision);
                    if (effectivePurgeRevision > 0 &&
                        !IsEntityNewerThanPurge(
                            responseContract,
                            effectivePurgeRevision))
                    {
                        continue;
                    }

                    var currentContract = currentManifest?
                        .Contracts
                        .FirstOrDefault(candidate =>
                            candidate.Id ==
                            responseContract.Id);
                    var sourceContract =
                        currentContract is not null &&
                        IsStrictlyNewer(
                            currentContract,
                            responseContract)
                            ? currentContract
                            : responseContract;
                    var contract = CloneWithoutContent(
                        sourceContract);
                    manifest.Contracts.Add(contract);
                    if (sourceContract.FileContent is
                        { Length: > 0 } fileContent)
                    {
                        manifest.PdfBindings.Add(
                            await PublishContentAddressedPdfAsync(
                                customerDirectory,
                                contract,
                                fileContent,
                                ct));
                        continue;
                    }

                    var currentBinding = currentManifest?
                        .PdfBindings
                        .FirstOrDefault(binding =>
                            binding.ContractId == contract.Id);
                    if (currentBinding is not null &&
                        currentContract is not null &&
                        ContractMetadataMatches(
                            currentContract,
                            contract) &&
                        await IsCachedPdfBindingValidAsync(
                            customerDirectory,
                            currentBinding,
                            ct))
                    {
                        manifest.PdfBindings.Add(
                            currentBinding.Clone());
                    }
                }

                var responseContractIds = manifest.Contracts
                    .Select(contract => contract.Id)
                    .ToHashSet();
                foreach (var omitted in currentManifest?
                             .Contracts
                             .Where(contract =>
                                 !responseContractIds.Contains(
                                     contract.Id)) ??
                         Enumerable.Empty<CustomerContractDto>())
                {
                    watermarks.ContractRevisions.TryGetValue(
                        omitted.Id,
                        out var contractPurgeRevision);
                    var effectivePurgeRevision = Math.Max(
                        customerPurgeRevision,
                        contractPurgeRevision);
                    if (effectivePurgeRevision > 0 &&
                        !IsEntityNewerThanPurge(
                            omitted,
                            effectivePurgeRevision))
                    {
                        continue;
                    }

                    manifest.Contracts.Add(
                        CloneWithoutContent(omitted));
                    var binding = currentManifest!.PdfBindings
                        .FirstOrDefault(candidate =>
                            candidate.ContractId == omitted.Id);
                    if (binding is not null &&
                        await IsCachedPdfBindingValidAsync(
                            customerDirectory,
                            binding,
                            ct))
                    {
                        manifest.PdfBindings.Add(binding.Clone());
                    }
                }

                ThrowIfOwnerSessionStale(ownerSession);
                await WriteJsonAtomicallyUnderLockAsync(
                    manifestPath,
                    manifest,
                    ct,
                    () => ThrowIfOwnerSessionStale(
                        ownerSession));
                await RefreshCompatibilityPdfMirrorsAsync(
                    customerDirectory,
                    manifest,
                    ct);
                PruneUnreferencedPdfObjects(
                    customerDirectory,
                    manifest);
            },
            ct);
    }

    public Task<IReadOnlyList<CustomerContractDto>>
        LoadContractsAsync(
            Guid customerId,
            CancellationToken ct = default)
        => LoadContractsAsync(
            CaptureOwnerSession(),
            customerId,
            ct);

    public async Task<IReadOnlyList<CustomerContractDto>>
        LoadContractsAsync(
            CacheOwnerSession ownerSession,
            Guid customerId,
            CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        var manifestPath = GetManifestPath(
            rootDirectory,
            customerId);
        var watermarkPath = GetPurgeWatermarkPath(rootDirectory);
        return await WithManifestWriteLocksAsync(
            [manifestPath, watermarkPath],
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                if (!File.Exists(manifestPath))
                {
                    return (IReadOnlyList<CustomerContractDto>)
                        Array.Empty<CustomerContractDto>();
                }

                var manifest =
                    await ReadAndValidateContractManifestAsync(
                        manifestPath,
                        ownerSession,
                        customerId,
                        ct);
                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                watermarks.CustomerRevisions.TryGetValue(
                    customerId,
                    out var customerPurgeRevision);
                ThrowIfOwnerSessionStale(ownerSession);
                return manifest.Contracts
                    .Where(contract =>
                    {
                        watermarks.ContractRevisions.TryGetValue(
                            contract.Id,
                            out var contractPurgeRevision);
                        var effectivePurgeRevision = Math.Max(
                            customerPurgeRevision,
                            contractPurgeRevision);
                        return effectivePurgeRevision <= 0 ||
                               IsEntityNewerThanPurge(
                                   contract,
                                   effectivePurgeRevision);
                    })
                    .Select(CloneWithoutContent)
                    .ToList();
            },
            ct);
    }

    public Task RemoveCustomerContractsAsync(
        Guid customerId,
        CancellationToken ct = default)
        => RemoveCustomerContractsAsync(
            CaptureOwnerSession(),
            customerId,
            ct);

    public async Task RemoveCustomerContractsAsync(
        CacheOwnerSession ownerSession,
        Guid customerId,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        await RemoveCustomerContractsUnderRootAsync(
            ownerSession,
            rootDirectory,
            customerId,
            ct);
    }

    private Task RemoveCustomerContractsUnderRootAsync(
        CacheOwnerSession ownerSession,
        string rootDirectory,
        Guid customerId,
        CancellationToken ct)
    {
        if (customerId == Guid.Empty)
            return Task.CompletedTask;

        var manifestPath = GetManifestPath(rootDirectory, customerId);
        return WithManifestWriteLockAsync(
            manifestPath,
            () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                ct.ThrowIfCancellationRequested();
                var customerDirectory = GetCustomerDirectory(
                    rootDirectory,
                    customerId);
                if (!Directory.Exists(customerDirectory))
                    return Task.CompletedTask;

                RejectReparsePoint(customerDirectory);
                Directory.Delete(customerDirectory, recursive: true);
                if (Directory.Exists(customerDirectory))
                {
                    throw new IOException(
                        $"Purged customer contract cache directory remains: {customerDirectory}");
                }

                return Task.CompletedTask;
            },
            ct);
    }

    public Task RemoveCustomerAsync(
        Guid customerId,
        long purgeRevision,
        CancellationToken ct = default)
        => RemoveCustomerAsync(
            CaptureOwnerSession(),
            customerId,
            purgeRevision,
            ct);

    public async Task RemoveCustomerAsync(
        CacheOwnerSession ownerSession,
        Guid customerId,
        long purgeRevision,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        if (customerId == Guid.Empty)
            return;

        var customersManifestPath =
            GetCustomersManifestPath(rootDirectory);
        var contractsManifestPath =
            GetManifestPath(rootDirectory, customerId);
        var watermarkPath =
            GetPurgeWatermarkPath(rootDirectory);
        await WithManifestWriteLocksAsync(
            [
                customersManifestPath,
                contractsManifestPath,
                watermarkPath
            ],
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                var watermarkExisted = File.Exists(watermarkPath);
                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                var originalWatermarks = watermarks.Clone();
                List<CustomerDto>? customers = null;
                if (File.Exists(customersManifestPath))
                {
                    customers =
                        await ReadRequiredJsonManifestAsync<List<CustomerDto>>(
                            customersManifestPath,
                            ct);
                }

                var originalCustomers = customers?
                    .Select(CloneCustomer)
                    .ToList();
                var watermarkChanged = false;
                var customersChanged = false;
                try
                {
                    if (!watermarks.CustomerRevisions.TryGetValue(
                            customerId,
                            out var previousPurgeRevision) ||
                        purgeRevision > previousPurgeRevision)
                    {
                        watermarks.CustomerRevisions[customerId] =
                            purgeRevision;
                        await WriteJsonAtomicallyUnderLockAsync(
                            watermarkPath,
                            watermarks,
                            ct,
                            () => ThrowIfOwnerSessionStale(
                                ownerSession));
                        watermarkChanged = true;
                    }

                    if (customers is not null &&
                        customers.Any(customer =>
                            customer.Id == customerId &&
                            IsCustomerNewerThanPurge(
                                customer,
                                purgeRevision)))
                    {
                        return;
                    }

                    if (File.Exists(contractsManifestPath))
                    {
                        var contracts =
                            await ReadAndValidateContractManifestAsync(
                                contractsManifestPath,
                                ownerSession,
                                customerId,
                                ct);

                        if (contracts.Contracts.Any(contract =>
                                IsEntityNewerThanPurge(
                                    contract,
                                    purgeRevision)))
                        {
                            return;
                        }
                    }

                    if (customers is not null)
                    {
                        var removedCount = customers.RemoveAll(customer =>
                            customer.Id == customerId &&
                            !IsCustomerNewerThanPurge(
                                customer,
                                purgeRevision));
                        if (removedCount > 0)
                        {
                            await WriteJsonAtomicallyUnderLockAsync(
                                customersManifestPath,
                                customers.Select(CloneCustomer).ToList(),
                                ct,
                                () => ThrowIfOwnerSessionStale(
                                    ownerSession));
                            customersChanged = true;
                        }
                    }

                    ct.ThrowIfCancellationRequested();
                    ThrowIfOwnerSessionStale(ownerSession);
                    var customerDirectory = GetCustomerDirectory(
                        rootDirectory,
                        customerId);
                    if (!Directory.Exists(customerDirectory))
                        return;

                    RejectReparsePoint(customerDirectory);
                    Directory.Delete(customerDirectory, recursive: true);
                    if (Directory.Exists(customerDirectory))
                    {
                        throw new IOException(
                            $"Purged customer contract cache directory remains: {customerDirectory}");
                    }
                }
                catch (Exception staleOwnerException)
                    when (ContainsStaleOwnerFailure(
                        staleOwnerException))
                {
                    var rollbackFailures = new List<Exception>();
                    if (customersChanged && originalCustomers is not null)
                    {
                        try
                        {
                            await WriteJsonAtomicallyUnderLockAsync(
                                customersManifestPath,
                                originalCustomers,
                                CancellationToken.None);
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (watermarkChanged)
                    {
                        try
                        {
                            if (watermarkExisted)
                            {
                                await WriteJsonAtomicallyUnderLockAsync(
                                    watermarkPath,
                                    originalWatermarks,
                                    CancellationToken.None);
                            }
                            else
                            {
                                DeleteFileRequired(watermarkPath);
                            }
                        }
                        catch (Exception rollbackException)
                        {
                            rollbackFailures.Add(rollbackException);
                        }
                    }

                    if (rollbackFailures.Count > 0)
                    {
                        throw new AggregateException(
                            "Stale-owner customer purge rollback failed.",
                            new Exception[] { staleOwnerException }
                                .Concat(rollbackFailures));
                    }

                    throw;
                }
            },
            ct);
    }

    public Task RemoveContractAsync(
        Guid contractId,
        long purgeRevision,
        CancellationToken ct = default)
        => RemoveContractAsync(
            CaptureOwnerSession(),
            contractId,
            purgeRevision,
            ct);

    public async Task RemoveContractAsync(
        CacheOwnerSession ownerSession,
        Guid contractId,
        long purgeRevision,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        if (contractId == Guid.Empty || !Directory.Exists(rootDirectory))
            return;

        var watermarkPath = GetPurgeWatermarkPath(rootDirectory);
        await WithManifestWriteLockAsync(
            watermarkPath,
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                if (!watermarks.ContractRevisions.TryGetValue(
                        contractId,
                        out var previousPurgeRevision) ||
                    purgeRevision > previousPurgeRevision)
                {
                    watermarks.ContractRevisions[contractId] =
                        purgeRevision;
                    await WriteJsonAtomicallyUnderLockAsync(
                        watermarkPath,
                        watermarks,
                        ct,
                        () => ThrowIfOwnerSessionStale(
                            ownerSession));
                }
            },
            ct);

        foreach (var customerDirectory in Directory.EnumerateDirectories(rootDirectory))
        {
            RejectReparsePoint(customerDirectory);
            if (!Guid.TryParseExact(
                    Path.GetFileName(customerDirectory),
                    "N",
                    out _))
            {
                continue;
            }

            var manifestPath = Path.Combine(customerDirectory, "contracts.json");
            await WithManifestWriteLocksAsync(
                [manifestPath, watermarkPath],
                async () =>
                {
                    ThrowIfOwnerSessionStale(ownerSession);
                    ct.ThrowIfCancellationRequested();
                    if (!File.Exists(manifestPath))
                        return;

                    var manifest =
                        await ReadAndValidateContractManifestAsync(
                            manifestPath,
                            ownerSession,
                            Guid.ParseExact(
                                Path.GetFileName(customerDirectory),
                                "N"),
                            ct);

                    var removedCount = manifest.Contracts.RemoveAll(contract =>
                        contract.Id == contractId &&
                        !IsEntityNewerThanPurge(contract, purgeRevision));
                    if (manifest.Contracts.Any(contract =>
                            contract.Id == contractId &&
                            IsEntityNewerThanPurge(contract, purgeRevision)))
                    {
                        return;
                    }

                    if (removedCount > 0)
                    {
                        manifest.PdfBindings.RemoveAll(binding =>
                            binding.ContractId == contractId);
                        manifest.GenerationId = Guid.NewGuid();
                        manifest.SavedAtUtc = DateTime.UtcNow;
                        await WriteJsonAtomicallyUnderLockAsync(
                            manifestPath,
                            manifest,
                            ct,
                            () => ThrowIfOwnerSessionStale(
                                ownerSession));
                    }

                    DeleteFileRequired(
                        Path.Combine(customerDirectory, $"{contractId:N}.pdf"));
                    PruneUnreferencedPdfObjectsRequired(
                        customerDirectory,
                        manifest);
                },
                ct);
        }
    }
    public Task<string?> EnsureCachedPdfAsync(
        Guid customerId,
        CustomerContractDto contract,
        CancellationToken ct = default)
        => EnsureCachedPdfAsync(
            CaptureOwnerSession(),
            customerId,
            contract,
            ct);

    public async Task<string?> EnsureCachedPdfAsync(
        CacheOwnerSession ownerSession,
        Guid customerId,
        CustomerContractDto contract,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        var manifestPath = GetManifestPath(
            rootDirectory,
            customerId);
        var watermarkPath = GetPurgeWatermarkPath(rootDirectory);
        return await WithManifestWriteLocksAsync(
            [manifestPath, watermarkPath],
            async () =>
            {
                ThrowIfOwnerSessionStale(ownerSession);
                if (!File.Exists(manifestPath))
                    return null;

                var manifest =
                    await ReadAndValidateContractManifestAsync(
                        manifestPath,
                        ownerSession,
                        customerId,
                        ct);
                var manifestContract = manifest.Contracts
                    .SingleOrDefault(candidate =>
                        candidate.Id == contract.Id);
                if (manifestContract is null ||
                    !ContractMetadataMatches(
                        manifestContract,
                        contract))
                {
                    return null;
                }

                var watermarks =
                    await LoadPurgeWatermarksUnderLockAsync(
                        watermarkPath,
                        ct);
                ThrowIfContractIsPurged(
                    watermarks,
                    customerId,
                    contract);
                var customerDirectory = GetCustomerDirectory(
                    rootDirectory,
                    customerId);
                var binding = manifest.PdfBindings
                    .SingleOrDefault(candidate =>
                        candidate.ContractId == contract.Id);
                if (binding is not null &&
                    await IsCachedPdfBindingValidAsync(
                        customerDirectory,
                        binding,
                        ct))
                {
                    return GetPdfObjectPath(
                        customerDirectory,
                        binding);
                }

                if (contract.FileContent is { Length: > 0 } fileContent)
                {
                    var newBinding =
                        await PublishContentAddressedPdfAsync(
                            customerDirectory,
                            contract,
                            fileContent,
                            ct);
                    manifest.PdfBindings.RemoveAll(candidate =>
                        candidate.ContractId == contract.Id);
                    manifest.PdfBindings.Add(newBinding);
                    manifest.GenerationId = Guid.NewGuid();
                    manifest.SavedAtUtc = DateTime.UtcNow;
                    ThrowIfOwnerSessionStale(ownerSession);
                    await WriteJsonAtomicallyUnderLockAsync(
                        manifestPath,
                        manifest,
                        ct,
                        () => ThrowIfOwnerSessionStale(
                            ownerSession));
                    await RefreshCompatibilityPdfMirrorAsync(
                        customerDirectory,
                        newBinding,
                        ct);
                    return GetPdfObjectPath(
                        customerDirectory,
                        newBinding);
                }

                return null;
            },
            ct);
    }

    public Task<string> CachePdfAsync(
        Guid customerId,
        CustomerContractDto contract,
        string sourcePath,
        CancellationToken ct = default)
        => CachePdfAsync(
            CaptureOwnerSession(),
            customerId,
            contract,
            sourcePath,
            ct);

    public async Task<string> CachePdfAsync(
        CacheOwnerSession ownerSession,
        Guid customerId,
        CustomerContractDto contract,
        string sourcePath,
        CancellationToken ct = default)
    {
        using var ownerCommitLease =
            await AcquireOwnerCommitLeaseAsync(ownerSession, ct);
        var rootDirectory = ResolveRootDirectory(
            ownerSession,
            requireCurrentSession: true);
        {
            if (string.IsNullOrWhiteSpace(sourcePath) ||
                !File.Exists(sourcePath))
            {
                throw new FileNotFoundException(
                    "Contract source PDF was not found.",
                    sourcePath);
            }

            var currentManifestPath = GetManifestPath(
                rootDirectory,
                customerId);
            var currentWatermarkPath =
                GetPurgeWatermarkPath(rootDirectory);
            return await WithManifestWriteLocksAsync(
                [currentManifestPath, currentWatermarkPath],
                async () =>
                {
                    ThrowIfOwnerSessionStale(ownerSession);
                    if (!File.Exists(currentManifestPath))
                    {
                        throw new InvalidDataException(
                            "Contract PDF cannot be cached without its current manifest.");
                    }

                    var manifest =
                        await ReadAndValidateContractManifestAsync(
                            currentManifestPath,
                            ownerSession,
                            customerId,
                            ct);
                    var manifestContract = manifest.Contracts
                        .SingleOrDefault(candidate =>
                            candidate.Id == contract.Id);
                    if (manifestContract is null ||
                        !ContractMetadataMatches(
                            manifestContract,
                            contract))
                    {
                        throw new InvalidDataException(
                            "Contract PDF metadata does not match the current manifest revision, hash, and size.");
                    }

                    var watermarks =
                        await LoadPurgeWatermarksUnderLockAsync(
                            currentWatermarkPath,
                            ct);
                    ThrowIfContractIsPurged(
                        watermarks,
                        customerId,
                        contract);
                    var customerDirectory = GetCustomerDirectory(
                        rootDirectory,
                        customerId);
                    var binding =
                        await PublishContentAddressedPdfAsync(
                            customerDirectory,
                            contract,
                            sourcePath,
                            ct);
                    manifest.PdfBindings.RemoveAll(candidate =>
                        candidate.ContractId == contract.Id);
                    manifest.PdfBindings.Add(binding);
                    manifest.GenerationId = Guid.NewGuid();
                    manifest.SavedAtUtc = DateTime.UtcNow;
                    ThrowIfOwnerSessionStale(ownerSession);
                    await WriteJsonAtomicallyUnderLockAsync(
                        currentManifestPath,
                        manifest,
                        ct,
                        () => ThrowIfOwnerSessionStale(
                            ownerSession));
                    await RefreshCompatibilityPdfMirrorAsync(
                        customerDirectory,
                        binding,
                        ct);
                    return GetPdfObjectPath(
                        customerDirectory,
                        binding);
                },
                ct);
        }
    }

    private static string GetCustomersManifestPath(string rootDirectory)
        => Path.Combine(rootDirectory, "customers.json");

    private static string GetPurgeWatermarkPath(
        string rootDirectory)
        => Path.Combine(
            rootDirectory,
            ".purge-watermarks.json");

    private static string GetCustomerDirectory(
        string rootDirectory,
        Guid customerId)
        => Path.Combine(rootDirectory, customerId.ToString("N"));

    private static string GetManifestPath(
        string rootDirectory,
        Guid customerId)
        => Path.Combine(
            GetCustomerDirectory(rootDirectory, customerId),
            "contracts.json");

    private static string GetPdfPath(
        string rootDirectory,
        Guid customerId,
        Guid contractId)
        => Path.Combine(
            GetCustomerDirectory(rootDirectory, customerId),
            $"{contractId:N}.pdf");

    private static string GetPdfObjectDirectory(
        string customerDirectory)
        => Path.Combine(customerDirectory, ".objects");

    private static string GetPdfObjectPath(
        string customerDirectory,
        CachedPdfBinding binding)
        => Path.Combine(
            GetPdfObjectDirectory(customerDirectory),
            binding.ObjectFileName);

    private static async Task<bool> IsCachedPdfValidAsync(string pdfPath, CustomerContractDto contract, CancellationToken ct)
    {
        if (!File.Exists(pdfPath))
            return false;

        var length = new FileInfo(pdfPath).Length;
        if (length <= 0)
            return false;

        if (contract.FileSize > 0 && length != contract.FileSize)
            return false;

        if (!string.IsNullOrWhiteSpace(contract.FileHash))
        {
            var actualSha256 = await ComputeSha256Async(pdfPath, ct);
            if (!string.Equals(actualSha256, contract.FileHash.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static bool IsEntityNewerThanPurge(CustomerContractDto contract, long purgeRevision)
        => !contract.IsDeleted && contract.Revision > purgeRevision;

    private static bool IsCustomerNewerThanPurge(CustomerDto customer, long purgeRevision)
        => !customer.IsDeleted && customer.Revision > purgeRevision;

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task<PurgeWatermarkManifest>
        LoadPurgeWatermarksUnderLockAsync(
            string watermarkPath,
            CancellationToken ct)
    {
        if (!File.Exists(watermarkPath))
            return new PurgeWatermarkManifest();

        var manifest =
            await ReadRequiredJsonManifestAsync<PurgeWatermarkManifest>(
                watermarkPath,
                ct);
        manifest.CustomerRevisions ??=
            new Dictionary<Guid, long>();
        manifest.ContractRevisions ??=
            new Dictionary<Guid, long>();
        if (!string.Equals(
                manifest.Schema,
                PurgeWatermarkSchema,
                StringComparison.Ordinal) ||
            manifest.CustomerRevisions.Any(pair =>
                pair.Key == Guid.Empty || pair.Value < 0) ||
            manifest.ContractRevisions.Any(pair =>
                pair.Key == Guid.Empty || pair.Value < 0))
        {
            throw new InvalidDataException(
                $"Cache purge watermark contains an invalid entry: {watermarkPath}");
        }

        return manifest;
    }

    private static void ThrowIfContractIsPurged(
        PurgeWatermarkManifest watermarks,
        Guid customerId,
        CustomerContractDto contract)
    {
        watermarks.CustomerRevisions.TryGetValue(
            customerId,
            out var customerPurgeRevision);
        watermarks.ContractRevisions.TryGetValue(
            contract.Id,
            out var contractPurgeRevision);
        var effectivePurgeRevision = Math.Max(
            customerPurgeRevision,
            contractPurgeRevision);
        if (effectivePurgeRevision > 0 &&
            !IsEntityNewerThanPurge(
                contract,
                effectivePurgeRevision))
        {
            throw new InvalidDataException(
                $"Contract cache write is older than its durable purge watermark: {contract.Id}");
        }
    }

    private async Task<CustomerContractCacheManifest>
        ReadAndValidateContractManifestAsync(
            string manifestPath,
            CacheOwnerSession ownerSession,
            Guid expectedCustomerId,
            CancellationToken ct)
    {
        var manifest =
            await ReadRequiredJsonManifestAsync<CustomerContractCacheManifest>(
                manifestPath,
                ct);
        manifest.Contracts ??= new List<CustomerContractDto>();
        manifest.PdfBindings ??= new List<CachedPdfBinding>();
        if (manifest.CustomerId != expectedCustomerId ||
            manifest.Contracts.Any(contract =>
                contract.Id == Guid.Empty ||
                contract.CustomerId != expectedCustomerId) ||
            manifest.Contracts
                .GroupBy(contract => contract.Id)
                .Any(group => group.Count() != 1) ||
            manifest.PdfBindings.Any(binding =>
                binding.ContractId == Guid.Empty ||
                binding.Revision < 0 ||
                binding.Size <= 0 ||
                !IsExactSha256(binding.Sha256) ||
                !string.Equals(
                    binding.ObjectFileName,
                    $"{binding.Sha256}.pdf",
                    StringComparison.Ordinal)) ||
            manifest.PdfBindings
                .GroupBy(binding => binding.ContractId)
                .Any(group => group.Count() != 1) ||
            manifest.PdfBindings.Any(binding =>
                !manifest.Contracts.Any(contract =>
                    contract.Id == binding.ContractId &&
                    ContractMetadataMatchesBinding(
                        contract,
                        binding))))
        {
            throw new InvalidDataException(
                $"Contract cache manifest has invalid customer, revision, or PDF bindings: {manifestPath}");
        }

        if (!ownerSession.IsExplicitRoot)
        {
            if (!string.Equals(
                    manifest.Schema,
                    CacheScopeSchema,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    manifest.OwnerHash,
                    ownerSession.OwnerHash,
                    StringComparison.Ordinal) ||
                manifest.GenerationId == Guid.Empty)
            {
                throw new InvalidDataException(
                    $"Contract cache manifest does not match its exact owner/generation: {manifestPath}");
            }
        }

        return manifest;
    }

    private static bool ContractMetadataMatches(
        CustomerContractDto left,
        CustomerContractDto right)
        => left.Id == right.Id &&
           left.CustomerId == right.CustomerId &&
           left.Revision == right.Revision &&
           left.FileSize == right.FileSize &&
           string.Equals(
               NormalizeHash(left.FileHash),
               NormalizeHash(right.FileHash),
               StringComparison.Ordinal);

    private static bool IsStrictlyNewer<T>(
        T candidate,
        T baseline)
        where T : SyncEntityDto
        => candidate.Revision > baseline.Revision ||
           candidate.Revision == baseline.Revision &&
           candidate.UpdatedAtUtc >
           baseline.UpdatedAtUtc;

    private static bool ContractMetadataMatchesBinding(
        CustomerContractDto contract,
        CachedPdfBinding binding)
        => contract.Id == binding.ContractId &&
           contract.Revision == binding.Revision &&
           (contract.FileSize <= 0 ||
            contract.FileSize == binding.Size) &&
           (string.IsNullOrWhiteSpace(contract.FileHash) ||
            string.Equals(
                NormalizeHash(contract.FileHash),
                binding.Sha256,
                StringComparison.Ordinal));

    private static string NormalizeHash(string? hash)
        => hash?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool IsExactSha256(string? hash)
        => hash is { Length: 64 } &&
           hash.All(character =>
               character is >= '0' and <= '9' or
                   >= 'a' and <= 'f');

    private async Task<CachedPdfBinding>
        PublishContentAddressedPdfAsync(
            string customerDirectory,
            CustomerContractDto contract,
            byte[] fileContent,
            CancellationToken ct)
    {
        var actualHash = Convert.ToHexString(
                SHA256.HashData(fileContent))
            .ToLowerInvariant();
        ValidatePdfMetadata(
            contract,
            fileContent.LongLength,
            actualHash);
        var binding = CreatePdfBinding(
            contract,
            fileContent.LongLength,
            actualHash);
        var objectPath = GetPdfObjectPath(
            customerDirectory,
            binding);
        if (!await IsCachedPdfBindingValidAsync(
                customerDirectory,
                binding,
                ct))
        {
            var validationContract = CreateBindingValidationContract(
                contract,
                binding);
            var published =
                await WritePdfAtomicallyUnderLockAsync(
                    objectPath,
                    validationContract,
                    async (destination, token) =>
                        await destination.WriteAsync(
                            fileContent,
                            token),
                    ct);
            if (!published)
            {
                throw new InvalidDataException(
                    $"Contract PDF content did not match its metadata: {contract.Id}");
            }
        }

        return binding;
    }

    private async Task<CachedPdfBinding>
        PublishContentAddressedPdfAsync(
            string customerDirectory,
            CustomerContractDto contract,
            string sourcePath,
            CancellationToken ct)
    {
        var size = new FileInfo(sourcePath).Length;
        var hash = await ComputeSha256Async(sourcePath, ct);
        ValidatePdfMetadata(contract, size, hash);
        var binding = CreatePdfBinding(
            contract,
            size,
            hash);
        var objectPath = GetPdfObjectPath(
            customerDirectory,
            binding);
        if (!await IsCachedPdfBindingValidAsync(
                customerDirectory,
                binding,
                ct))
        {
            var validationContract = CreateBindingValidationContract(
                contract,
                binding);
            var published =
                await WritePdfAtomicallyUnderLockAsync(
                    objectPath,
                    validationContract,
                    async (destination, token) =>
                    {
                        await using var source =
                            File.OpenRead(sourcePath);
                        await source.CopyToAsync(
                            destination,
                            token);
                    },
                    ct);
            if (!published)
            {
                throw new InvalidDataException(
                    $"Contract PDF content did not match its metadata: {contract.Id}");
            }
        }

        return binding;
    }

    private static void ValidatePdfMetadata(
        CustomerContractDto contract,
        long actualSize,
        string actualHash)
    {
        if (actualSize <= 0 ||
            contract.FileSize > 0 &&
            contract.FileSize != actualSize ||
            !string.IsNullOrWhiteSpace(contract.FileHash) &&
            !string.Equals(
                NormalizeHash(contract.FileHash),
                actualHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Contract PDF content does not match its declared size/hash: {contract.Id}");
        }
    }

    private static CachedPdfBinding CreatePdfBinding(
        CustomerContractDto contract,
        long size,
        string hash)
        => new()
        {
            ContractId = contract.Id,
            Revision = contract.Revision,
            Size = size,
            Sha256 = hash,
            ObjectFileName = $"{hash}.pdf"
        };

    private static CustomerContractDto
        CreateBindingValidationContract(
            CustomerContractDto contract,
            CachedPdfBinding binding)
        => new()
        {
            Id = contract.Id,
            CustomerId = contract.CustomerId,
            Revision = contract.Revision,
            FileName = contract.FileName,
            FileSize = binding.Size,
            FileHash = binding.Sha256
        };

    private static async Task<bool>
        IsCachedPdfBindingValidAsync(
            string customerDirectory,
            CachedPdfBinding binding,
            CancellationToken ct)
    {
        var objectPath = GetPdfObjectPath(
            customerDirectory,
            binding);
        return await IsCachedPdfValidAsync(
            objectPath,
            new CustomerContractDto
            {
                Id = binding.ContractId,
                Revision = binding.Revision,
                FileSize = binding.Size,
                FileHash = binding.Sha256
            },
            ct);
    }

    private async Task RefreshCompatibilityPdfMirrorsAsync(
        string customerDirectory,
        CustomerContractCacheManifest manifest,
        CancellationToken ct)
    {
        foreach (var binding in manifest.PdfBindings)
        {
            await RefreshCompatibilityPdfMirrorAsync(
                customerDirectory,
                binding,
                ct);
        }

        var retainedContractIds = manifest.Contracts
            .Select(contract => contract.Id)
            .ToHashSet();
        foreach (var path in Directory.EnumerateFiles(
                     customerDirectory,
                     "*.pdf",
                     SearchOption.TopDirectoryOnly))
        {
            if (Guid.TryParseExact(
                    Path.GetFileNameWithoutExtension(path),
                    "N",
                    out var contractId) &&
                !retainedContractIds.Contains(contractId))
            {
                DeleteFileRequired(path);
            }
        }
    }

    private async Task RefreshCompatibilityPdfMirrorAsync(
        string customerDirectory,
        CachedPdfBinding binding,
        CancellationToken ct)
    {
        var objectPath = GetPdfObjectPath(
            customerDirectory,
            binding);
        var compatibilityPath = Path.Combine(
            customerDirectory,
            $"{binding.ContractId:N}.pdf");
        var validationContract = new CustomerContractDto
        {
            Id = binding.ContractId,
            Revision = binding.Revision,
            FileSize = binding.Size,
            FileHash = binding.Sha256
        };
        if (await IsCachedPdfValidAsync(
                compatibilityPath,
                validationContract,
                ct))
        {
            return;
        }

        var published =
            await WritePdfAtomicallyUnderLockAsync(
                compatibilityPath,
                validationContract,
                async (destination, token) =>
                {
                    await using var source =
                        File.OpenRead(objectPath);
                    await source.CopyToAsync(
                        destination,
                        token);
                },
                ct,
                invokeBeforePdfWriteHook: false);
        if (!published)
        {
            throw new InvalidDataException(
                $"Compatibility PDF mirror validation failed: {binding.ContractId}");
        }
    }

    private static void PruneUnreferencedPdfObjects(
        string customerDirectory,
        CustomerContractCacheManifest manifest)
    {
        var objectDirectory = GetPdfObjectDirectory(
            customerDirectory);
        if (!Directory.Exists(objectDirectory))
            return;

        var retained = manifest.PdfBindings
            .Select(binding => binding.ObjectFileName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var objectPath in Directory.EnumerateFiles(
                     objectDirectory,
                     "*.pdf",
                     SearchOption.TopDirectoryOnly))
        {
            if (!retained.Contains(Path.GetFileName(objectPath)))
                TryDeleteFile(objectPath);
        }
    }

    private static void
        PruneUnreferencedPdfObjectsRequired(
            string customerDirectory,
            CustomerContractCacheManifest manifest)
    {
        var objectDirectory = GetPdfObjectDirectory(
            customerDirectory);
        if (!Directory.Exists(objectDirectory))
            return;

        var retained = manifest.PdfBindings
            .Select(binding => binding.ObjectFileName)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var objectPath in Directory.EnumerateFiles(
                     objectDirectory,
                     "*.pdf",
                     SearchOption.TopDirectoryOnly))
        {
            if (!retained.Contains(
                    Path.GetFileName(objectPath)))
            {
                DeleteFileRequired(objectPath);
            }
        }
    }

    private async Task<T> ReadRequiredJsonManifestAsync<T>(
        string manifestPath,
        CancellationToken ct)
        where T : class
    {
        await using var stream = new FileStream(
            manifestPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            useAsync: true);
        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   _jsonOptions,
                   ct)
               ?? throw new InvalidDataException(
                   $"Cache manifest root is null: {manifestPath}");
    }

    private static async Task WithManifestWriteLockAsync(
        string targetPath,
        Func<Task> operation,
        CancellationToken ct)
        => await WithManifestWriteLocksAsync(
            [targetPath],
            operation,
            ct);

    private static async Task WithManifestWriteLocksAsync(
        IReadOnlyList<string> targetPaths,
        Func<Task> operation,
        CancellationToken ct)
    {
        var normalizedTargetPaths = targetPaths
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(
                static path => path,
                StringComparer.OrdinalIgnoreCase)
            .ToList();
        var acquiredInProcessLocks =
            new List<(string Path, RefCountedManifestLock Entry)>();
        var acquiredProcessLeases = new List<FileStream>();
        var failures = new List<Exception>();
        try
        {
            foreach (var normalizedTargetPath in normalizedTargetPaths)
            {
                var manifestLock = RentManifestWriteLock(
                    normalizedTargetPath);
                try
                {
                    await manifestLock.Gate.WaitAsync(ct);
                    acquiredInProcessLocks.Add((
                        normalizedTargetPath,
                        manifestLock));
                }
                catch
                {
                    ReturnManifestWriteLock(
                        normalizedTargetPath,
                        manifestLock,
                        releaseGate: false);
                    throw;
                }
            }

            foreach (var normalizedTargetPath in normalizedTargetPaths)
            {
                acquiredProcessLeases.Add(
                    await AcquireManifestProcessLeaseAsync(
                        normalizedTargetPath,
                        ct));
            }

            foreach (var normalizedTargetPath in normalizedTargetPaths)
            {
                var directoryPath = Path.GetDirectoryName(
                        normalizedTargetPath)
                    ?? throw new InvalidOperationException(
                        $"Cache manifest has no parent directory: {normalizedTargetPath}");
                Directory.CreateDirectory(directoryPath);
                RejectReparsePoint(directoryPath);
                if (File.Exists(normalizedTargetPath))
                    RejectReparsePoint(normalizedTargetPath);
                RecoverOwnedAtomicPublishResidues(
                    directoryPath,
                    Path.GetFileName(normalizedTargetPath));
            }

            await operation();
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            for (var index = acquiredProcessLeases.Count - 1;
                 index >= 0;
                 index--)
            {
                try
                {
                    await acquiredProcessLeases[index].DisposeAsync();
                }
                catch (Exception ex)
                {
                    failures.Add(ex);
                }
            }

            for (var index = acquiredInProcessLocks.Count - 1;
                 index >= 0;
                 index--)
            {
                var acquired =
                    acquiredInProcessLocks[index];
                ReturnManifestWriteLock(
                    acquired.Path,
                    acquired.Entry,
                    releaseGate: true);
            }
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Cache manifest operation and lease cleanup failed.",
                failures);
        }
    }

    private static async Task<TResult> WithManifestWriteLockAsync<TResult>(
        string targetPath,
        Func<Task<TResult>> operation,
        CancellationToken ct)
    {
        TResult? result = default;
        Func<Task> wrapper = async () =>
        {
            result = await operation();
        };
        await WithManifestWriteLocksAsync(
            [targetPath],
            wrapper,
            ct);
        return result!;
    }

    private static async Task<TResult>
        WithManifestWriteLocksAsync<TResult>(
            IReadOnlyList<string> targetPaths,
            Func<Task<TResult>> operation,
        CancellationToken ct)
    {
        TResult? result = default;
        Func<Task> wrapper = async () =>
        {
            result = await operation();
        };
        await WithManifestWriteLocksAsync(
            targetPaths,
            wrapper,
            ct);
        return result!;
    }

    private static RefCountedManifestLock RentManifestWriteLock(
        string normalizedTargetPath)
    {
        while (true)
        {
            var entry = ManifestWriteLocks.GetOrAdd(
                normalizedTargetPath,
                static _ => new RefCountedManifestLock());
            lock (entry.SyncRoot)
            {
                if (entry.Retired)
                    continue;

                entry.ReferenceCount++;
                return entry;
            }
        }
    }

    private static void ReturnManifestWriteLock(
        string normalizedTargetPath,
        RefCountedManifestLock entry,
        bool releaseGate)
    {
        if (releaseGate)
            entry.Gate.Release();

        lock (entry.SyncRoot)
        {
            entry.ReferenceCount--;
            if (entry.ReferenceCount < 0)
            {
                throw new InvalidOperationException(
                    $"Cache manifest lock reference count underflowed: {normalizedTargetPath}");
            }

            if (entry.ReferenceCount != 0)
                return;

            entry.Retired = true;
            ManifestWriteLocks.TryRemove(
                new KeyValuePair<string, RefCountedManifestLock>(
                    normalizedTargetPath,
                    entry));
        }
    }

    private static async Task<FileStream> AcquireManifestProcessLeaseAsync(
        string normalizedTargetPath,
        CancellationToken ct)
    {
        var lockDirectory = GetManifestLockDirectory(
            normalizedTargetPath);
        Directory.CreateDirectory(lockDirectory);
        RejectReparsePoint(lockDirectory);

        var bindingPath = NormalizeManifestLeaseBinding(
            normalizedTargetPath);
        var bindingBytes = System.Text.Encoding.UTF8.GetBytes(
            $"{ManifestLeaseSchema}\n{bindingPath}");
        var targetHash = Convert.ToHexString(
                SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes(bindingPath)))
            .ToLowerInvariant();
        var lockPath = Path.Combine(
            lockDirectory,
            $"{targetHash}.lock");
        var started = Stopwatch.StartNew();
        FileStream processLease;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                processLease = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    4 * 1024,
                    FileOptions.Asynchronous |
                    FileOptions.WriteThrough);
                break;
            }
            catch (IOException ex)
            {
                if (started.Elapsed >= TimeSpan.FromSeconds(30))
                {
                    throw new IOException(
                        $"Timed out waiting for cache manifest lease: {normalizedTargetPath}",
                        ex);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
            }
        }

        try
        {
            RejectReparsePoint(lockPath);
            var openedLockPath = Path.GetFullPath(processLease.Name);
            if (!string.Equals(
                    openedLockPath,
                    Path.GetFullPath(lockPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Cache manifest lease opened an unexpected path: {openedLockPath}");
            }

            if (processLease.Length > 0 &&
                processLease.Length < bindingBytes.LongLength)
            {
                var partialBinding =
                    new byte[(int)processLease.Length];
                processLease.Position = 0;
                await processLease.ReadExactlyAsync(
                    partialBinding,
                    ct);
                var quarantineDirectory = Path.Combine(
                    lockDirectory,
                    ".quarantine");
                Directory.CreateDirectory(
                    quarantineDirectory);
                RejectReparsePoint(quarantineDirectory);
                var quarantinePath = Path.Combine(
                    quarantineDirectory,
                    $"{Path.GetFileNameWithoutExtension(lockPath)}.{Guid.NewGuid():N}.partial");
                await using (var quarantine = new FileStream(
                                 quarantinePath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 4 * 1024,
                                 FileOptions.Asynchronous |
                                 FileOptions.WriteThrough))
                {
                    await quarantine.WriteAsync(
                        partialBinding,
                        ct);
                    await quarantine.FlushAsync(ct);
                    quarantine.Flush(flushToDisk: true);
                }

                processLease.SetLength(0);
                processLease.Position = 0;
            }

            if (processLease.Length == 0)
            {
                await processLease.WriteAsync(bindingBytes, ct);
                await processLease.FlushAsync(ct);
                processLease.Flush(flushToDisk: true);
            }
            else
            {
                if (processLease.Length != bindingBytes.LongLength)
                {
                    throw new InvalidDataException(
                        $"Cache manifest lease binding length is invalid: {lockPath}");
                }

                var existingBinding = new byte[bindingBytes.Length];
                processLease.Position = 0;
                await processLease.ReadExactlyAsync(
                    existingBinding,
                    ct);
                if (!existingBinding.AsSpan().SequenceEqual(bindingBytes))
                {
                    throw new InvalidDataException(
                        $"Cache manifest lease is bound to another target: {lockPath}");
                }
            }

            processLease.Position = processLease.Length;
            return processLease;
        }
        catch
        {
            await processLease.DisposeAsync();
            throw;
        }
    }

    private static string GetManifestLockDirectory(
        string normalizedTargetPath)
    {
        var targetDirectory = Path.GetDirectoryName(normalizedTargetPath)
            ?? throw new InvalidOperationException(
                $"Cache manifest has no parent directory: {normalizedTargetPath}");
        var targetFileName = Path.GetFileName(normalizedTargetPath);
        var cacheRoot = string.Equals(
                targetFileName,
                "customers.json",
                StringComparison.Ordinal)
            ? targetDirectory
            : string.Equals(
                targetFileName,
                ".purge-watermarks.json",
                StringComparison.Ordinal)
                ? targetDirectory
            : string.Equals(
                targetFileName,
                "contracts.json",
                StringComparison.Ordinal)
                ? Path.GetDirectoryName(targetDirectory)
                    ?? throw new InvalidOperationException(
                        $"Contract cache manifest has no cache root: {normalizedTargetPath}")
                : throw new InvalidOperationException(
                    $"Unsupported cache manifest lease target: {normalizedTargetPath}");
        var normalizedCacheRoot = Path.GetFullPath(cacheRoot);
        if (Directory.Exists(normalizedCacheRoot))
            RejectReparsePoint(normalizedCacheRoot);
        if (Directory.Exists(targetDirectory))
            RejectReparsePoint(targetDirectory);
        var relativeTarget = Path.GetRelativePath(
            normalizedCacheRoot,
            normalizedTargetPath);
        if (Path.IsPathRooted(relativeTarget) ||
            relativeTarget.Equals("..", StringComparison.Ordinal) ||
            relativeTarget.StartsWith(
                $"..{Path.DirectorySeparatorChar}",
                StringComparison.Ordinal))
        {
            throw new IOException(
                $"Cache manifest lease target escaped its cache root: {normalizedTargetPath}");
        }

        return Path.Combine(
            normalizedCacheRoot,
            ".manifest-locks");
    }

    private static string NormalizeManifestLeaseBinding(string targetPath)
    {
        var normalized = Path.GetFullPath(targetPath);
        return OperatingSystem.IsWindows()
            ? normalized.ToUpperInvariant()
            : normalized;
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new IOException(
                $"Cache manifest lease path must not be a reparse point: {path}");
        }
    }

    private async Task<bool> WritePdfAtomicallyUnderLockAsync(
        string targetPath,
        CustomerContractDto contract,
        Func<FileStream, CancellationToken, Task> writeContentAsync,
        CancellationToken ct,
        bool invokeBeforePdfWriteHook = true)
    {
        var directoryPath = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                $"Cache PDF has no parent directory: {targetPath}");
        Directory.CreateDirectory(directoryPath);
        RejectReparsePoint(directoryPath);
        if (File.Exists(targetPath))
            RejectReparsePoint(targetPath);

        var fileName = Path.GetFileName(targetPath);
        RecoverOwnedAtomicPublishResidues(
            directoryPath,
            fileName);
        var operationId = Guid.NewGuid();
        var operationIdText = operationId.ToString("N");
        var ownedPrefix =
            $".{fileName}.{AtomicOwnerSchema}.{operationIdText}";
        var temporaryPath = Path.Combine(
            directoryPath,
            $"{ownedPrefix}.tmp");
        var backupPath = Path.Combine(
            directoryPath,
            $"{ownedPrefix}.bak");
        var ownerMarkerPath = Path.Combine(
            directoryPath,
            $"{ownedPrefix}.owner.json");
        var failures = new List<Exception>();
        FileStream? ownerMarkerLease = null;
        var published = false;

        try
        {
            ownerMarkerLease = await CreateAtomicOwnerMarkerLeaseAsync(
                ownerMarkerPath,
                new AtomicOwnerMarker
                {
                    Schema = AtomicOwnerSchema,
                    TargetFileName = fileName,
                    OperationId = operationId
                },
                ct);

            await using (var temporaryStream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.WriteThrough))
            {
                await writeContentAsync(temporaryStream, ct);
                await temporaryStream.FlushAsync(ct);
                temporaryStream.Flush(flushToDisk: true);
            }

            if (await IsCachedPdfValidAsync(
                    temporaryPath,
                    contract,
                    ct))
            {
                RejectReparsePoint(directoryPath);
                if (File.Exists(targetPath))
                    RejectReparsePoint(targetPath);
                if (invokeBeforePdfWriteHook &&
                    _beforePdfWriteAsync is not null)
                    await _beforePdfWriteAsync(targetPath, ct);

                RejectReparsePoint(directoryPath);
                if (File.Exists(targetPath))
                    RejectReparsePoint(targetPath);
                if (File.Exists(targetPath))
                {
                    File.Replace(
                        temporaryPath,
                        targetPath,
                        backupPath,
                        ignoreMetadataErrors: true);
                    DeleteFileRequired(backupPath);
                }
                else
                {
                    File.Move(temporaryPath, targetPath);
                }

                if (!File.Exists(targetPath))
                {
                    throw new IOException(
                        $"Atomic cache PDF publish did not create its target: {targetPath}");
                }
                RejectReparsePoint(directoryPath);
                RejectReparsePoint(targetPath);

                published = true;
            }
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            var residueCleanupSucceeded = true;
            foreach (var residuePath in new[] { temporaryPath, backupPath })
            {
                try
                {
                    DeleteFileRequired(residuePath);
                }
                catch (Exception cleanupEx)
                {
                    residueCleanupSucceeded = false;
                    failures.Add(cleanupEx);
                }
            }

            if (ownerMarkerLease is not null)
            {
                try
                {
                    await ownerMarkerLease.DisposeAsync();
                }
                catch (Exception cleanupEx)
                {
                    residueCleanupSucceeded = false;
                    failures.Add(cleanupEx);
                }
            }

            if (residueCleanupSucceeded)
            {
                try
                {
                    DeleteFileRequired(ownerMarkerPath);
                }
                catch (Exception cleanupEx)
                {
                    failures.Add(cleanupEx);
                }
            }
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
        {
            throw new AggregateException(
                "Atomic cache PDF publish and cleanup failed.",
                failures);
        }

        return published;
    }

    private async Task WriteJsonAtomicallyUnderLockAsync<T>(
        string targetPath,
        T value,
        CancellationToken ct,
        Action? validateBeforeCommit = null)
    {
        var directoryPath = Path.GetDirectoryName(targetPath)
            ?? throw new InvalidOperationException(
                $"Cache manifest has no parent directory: {targetPath}");
        Directory.CreateDirectory(directoryPath);
        RejectReparsePoint(directoryPath);
        if (File.Exists(targetPath))
            RejectReparsePoint(targetPath);

        var fileName = Path.GetFileName(targetPath);
        var operationId = Guid.NewGuid();
        var operationIdText = operationId.ToString("N");
        var ownedPrefix =
            $".{fileName}.{AtomicOwnerSchema}.{operationIdText}";
        var temporaryPath = Path.Combine(
            directoryPath,
            $"{ownedPrefix}.tmp");
        var backupPath = Path.Combine(
            directoryPath,
            $"{ownedPrefix}.bak");
        var ownerMarkerPath = Path.Combine(
            directoryPath,
            $"{ownedPrefix}.owner.json");
        var failures = new List<Exception>();
        FileStream? ownerMarkerLease = null;

        try
        {
            ownerMarkerLease = await CreateAtomicOwnerMarkerLeaseAsync(
                ownerMarkerPath,
                new AtomicOwnerMarker
                {
                    Schema = AtomicOwnerSchema,
                    TargetFileName = fileName,
                    OperationId = operationId
                },
                ct);

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    _jsonOptions,
                    ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            if (_beforeAtomicPublishAsync is not null)
                await _beforeAtomicPublishAsync(targetPath, ct);

            validateBeforeCommit?.Invoke();
            RejectReparsePoint(directoryPath);
            if (File.Exists(targetPath))
                RejectReparsePoint(targetPath);
            if (File.Exists(targetPath))
            {
                File.Replace(
                    temporaryPath,
                    targetPath,
                    backupPath,
                    ignoreMetadataErrors: true);
                DeleteFileRequired(backupPath);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }

            if (!File.Exists(targetPath))
                throw new IOException(
                    $"Atomic cache manifest publish did not create its target: {targetPath}");
            RejectReparsePoint(directoryPath);
            RejectReparsePoint(targetPath);
        }
        catch (Exception ex)
        {
            failures.Add(ex);
        }
        finally
        {
            var residueCleanupSucceeded = true;
            foreach (var residuePath in new[] { temporaryPath, backupPath })
            {
                try
                {
                    DeleteFileRequired(residuePath);
                }
                catch (Exception cleanupEx)
                {
                    residueCleanupSucceeded = false;
                    failures.Add(cleanupEx);
                }
            }

            if (ownerMarkerLease is not null)
            {
                try
                {
                    await ownerMarkerLease.DisposeAsync();
                }
                catch (Exception cleanupEx)
                {
                    residueCleanupSucceeded = false;
                    failures.Add(cleanupEx);
                }
            }

            if (residueCleanupSucceeded)
            {
                try
                {
                    DeleteFileRequired(ownerMarkerPath);
                }
                catch (Exception cleanupEx)
                {
                    failures.Add(cleanupEx);
                }
            }
        }

        if (failures.Count == 1)
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        if (failures.Count > 1)
            throw new AggregateException(
                "Atomic cache manifest publish and cleanup failed.",
                failures);
    }

    private async Task<FileStream> CreateAtomicOwnerMarkerLeaseAsync(
        string markerPath,
        AtomicOwnerMarker marker,
        CancellationToken ct)
    {
        var markerStream = new FileStream(
            markerPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4 * 1024,
            useAsync: true);
        try
        {
            await JsonSerializer.SerializeAsync(
                markerStream,
                marker,
                _jsonOptions,
                ct);
            await markerStream.FlushAsync(ct);
            markerStream.Flush(flushToDisk: true);
            return markerStream;
        }
        catch
        {
            await markerStream.DisposeAsync();
            throw;
        }
    }

    private static void RecoverOwnedAtomicPublishResidues(
        string directoryPath,
        string targetFileName)
    {
        var markerPrefix = $".{targetFileName}.{AtomicOwnerSchema}.";
        const string markerSuffix = ".owner.json";
        var recoveryEntries = new List<AtomicRecoveryEntry>();
        var markerPaths = Directory.EnumerateFiles(
                directoryPath,
                $"{markerPrefix}*{markerSuffix}",
                SearchOption.TopDirectoryOnly)
            .OrderBy(
                static path => Path.GetFileName(path),
                StringComparer.Ordinal)
            .ToList();
        foreach (var markerPath in markerPaths)
        {
            var markerFileName = Path.GetFileName(markerPath);
            if (!markerFileName.StartsWith(
                    markerPrefix,
                    StringComparison.Ordinal) ||
                !markerFileName.EndsWith(
                    markerSuffix,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Atomic cache owner marker filename is not exact: {markerPath}");
            }

            var operationIdText = markerFileName[
                markerPrefix.Length..
                ^markerSuffix.Length];
            if (!Guid.TryParseExact(
                    operationIdText,
                    "N",
                    out var operationId) ||
                !string.Equals(
                    operationId.ToString("N"),
                    operationIdText,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Atomic cache owner marker has an invalid operation id: {markerPath}");
            }

            AtomicOwnerMarker marker;
            try
            {
                using var markerStream = new FileStream(
                    markerPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.None,
                    4 * 1024,
                    useAsync: false);
                marker = ReadAtomicOwnerMarkerStrict(
                    markerStream,
                    markerPath);
            }
            catch (JsonException ex)
            {
                var crashOwnedPrefix =
                    $".{targetFileName}.{AtomicOwnerSchema}.{operationIdText}";
                QuarantineIncompleteAtomicEntry(
                    directoryPath,
                    markerPath,
                    Path.Combine(
                        directoryPath,
                        $"{crashOwnedPrefix}.tmp"),
                    Path.Combine(
                        directoryPath,
                        $"{crashOwnedPrefix}.bak"),
                    ex);
                continue;
            }

            if (!string.Equals(
                    marker.Schema,
                    AtomicOwnerSchema,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    marker.TargetFileName,
                    targetFileName,
                    StringComparison.Ordinal) ||
                marker.OperationId != operationId)
            {
                throw new InvalidDataException(
                    $"Atomic cache owner marker does not match its file identity: {markerPath}");
            }

            var ownedPrefix =
                $".{targetFileName}.{AtomicOwnerSchema}.{operationIdText}";
            recoveryEntries.Add(new AtomicRecoveryEntry
            {
                MarkerPath = markerPath,
                TemporaryPath = Path.Combine(
                    directoryPath,
                    $"{ownedPrefix}.tmp"),
                BackupPath = Path.Combine(
                    directoryPath,
                    $"{ownedPrefix}.bak")
            });
        }

        var ownedResiduePaths = recoveryEntries
            .SelectMany(static entry => new[]
            {
                entry.TemporaryPath,
                entry.BackupPath
            })
            .ToHashSet(StringComparer.Ordinal);
        foreach (var extension in new[] { "tmp", "bak" })
        {
            foreach (var managedResiduePath in Directory.EnumerateFiles(
                         directoryPath,
                         $".{targetFileName}.{AtomicOwnerSchema}.*.{extension}",
                         SearchOption.TopDirectoryOnly))
            {
                if (!ownedResiduePaths.Contains(managedResiduePath))
                {
                    throw new IOException(
                        $"Atomic cache residue has no valid owner marker: {managedResiduePath}");
                }
            }
        }

        foreach (var entry in recoveryEntries)
        {
            DeleteFileRequired(entry.TemporaryPath);
            DeleteFileRequired(entry.BackupPath);
            DeleteFileRequired(entry.MarkerPath);
        }
    }

    private static void QuarantineIncompleteAtomicEntry(
        string directoryPath,
        string markerPath,
        string temporaryPath,
        string backupPath,
        JsonException parseFailure)
    {
        var quarantineDirectory = Path.Combine(
            directoryPath,
            ".atomic-quarantine");
        Directory.CreateDirectory(quarantineDirectory);
        RejectReparsePoint(quarantineDirectory);
        var quarantineId = Guid.NewGuid().ToString("N");
        var moved = 0;
        foreach (var sourcePath in new[]
                 {
                     markerPath,
                     temporaryPath,
                     backupPath
                 })
        {
            if (!File.Exists(sourcePath))
                continue;

            var destinationPath = Path.Combine(
                quarantineDirectory,
                $"{quarantineId}.{Path.GetFileName(sourcePath)}.quarantined");
            File.Move(sourcePath, destinationPath);
            moved++;
        }

        if (moved == 0)
        {
            throw new InvalidDataException(
                $"Atomic cache owner marker became unavailable during crash recovery: {markerPath}",
                parseFailure);
        }
    }

    private static AtomicOwnerMarker ReadAtomicOwnerMarkerStrict(
        Stream markerStream,
        string markerPath)
    {
        using var document = JsonDocument.Parse(
            markerStream,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Atomic cache owner marker root must be an object: {markerPath}");
        }

        string? schema = null;
        string? targetFileName = null;
        string? operationIdText = null;
        var propertyNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!propertyNames.Add(property.Name))
            {
                throw new InvalidDataException(
                    $"Atomic cache owner marker has a duplicate property '{property.Name}': {markerPath}");
            }

            switch (property.Name)
            {
                case "schema":
                    schema = ReadRequiredMarkerString(
                        property,
                        markerPath);
                    break;
                case "targetFileName":
                    targetFileName = ReadRequiredMarkerString(
                        property,
                        markerPath);
                    break;
                case "operationId":
                    operationIdText = ReadRequiredMarkerString(
                        property,
                        markerPath);
                    break;
                default:
                    throw new InvalidDataException(
                        $"Atomic cache owner marker has an unknown property '{property.Name}': {markerPath}");
            }
        }

        if (propertyNames.Count != 3 ||
            schema is null ||
            targetFileName is null ||
            operationIdText is null ||
            !Guid.TryParseExact(
                operationIdText,
                "D",
                out var operationId) ||
            !string.Equals(
                operationId.ToString("D"),
                operationIdText,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Atomic cache owner marker schema is incomplete or invalid: {markerPath}");
        }

        return new AtomicOwnerMarker
        {
            Schema = schema,
            TargetFileName = targetFileName,
            OperationId = operationId
        };
    }

    private static string ReadRequiredMarkerString(
        JsonProperty property,
        string markerPath)
    {
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException(
                $"Atomic cache owner marker property '{property.Name}' must be a string: {markerPath}");
        }

        return property.Value.GetString()
            ?? throw new InvalidDataException(
                $"Atomic cache owner marker property '{property.Name}' must not be null: {markerPath}");
    }

    private static void DeleteFileRequired(string path)
    {
        if (!File.Exists(path))
            return;

        File.Delete(path);
        if (File.Exists(path))
            throw new IOException($"Cache file remains after deletion: {path}");
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cache cleanup failure should not block a fresh server download attempt.
        }
    }

    private static CustomerContractDto CloneWithoutContent(CustomerContractDto contract)
    {
        return new CustomerContractDto
        {
            Id = contract.Id,
            IsDeleted = contract.IsDeleted,
            CreatedAtUtc = contract.CreatedAtUtc,
            UpdatedAtUtc = contract.UpdatedAtUtc,
            Revision = contract.Revision,
            ExpectedRevision = contract.ExpectedRevision,
            MutationId = contract.MutationId,
            MutationCreatedAtUtc = contract.MutationCreatedAtUtc,
            CustomerId = contract.CustomerId,
            ContractType = contract.ContractType,
            FileName = contract.FileName,
            MimeType = contract.MimeType,
            FileSize = contract.FileSize,
            FileHash = contract.FileHash,
            Description = contract.Description,
            SignedDate = contract.SignedDate,
            ExpireDate = contract.ExpireDate,
            IsPrimary = contract.IsPrimary,
            UploadedByUsername = contract.UploadedByUsername,
            UploadedAtUtc = contract.UploadedAtUtc,
            FileContent = Array.Empty<byte>()
        };
    }

    private static CustomerDto CloneCustomer(CustomerDto customer)
    {
        return new CustomerDto
        {
            Id = customer.Id,
            IsDeleted = customer.IsDeleted,
            CreatedAtUtc = customer.CreatedAtUtc,
            UpdatedAtUtc = customer.UpdatedAtUtc,
            Revision = customer.Revision,
            ExpectedRevision = customer.ExpectedRevision,
            MutationId = customer.MutationId,
            MutationCreatedAtUtc = customer.MutationCreatedAtUtc,
            CustomerMasterId = customer.CustomerMasterId,
            TenantCode = customer.TenantCode,
            OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode,
            NameOriginal = customer.NameOriginal,
            NameMatchKey = customer.NameMatchKey,
            CategoryId = customer.CategoryId,
            TradeType = customer.TradeType,
            Department = customer.Department,
            ContactPerson = customer.ContactPerson,
            Representative = customer.Representative,
            BusinessNumber = customer.BusinessNumber,
            BusinessType = customer.BusinessType,
            BusinessItem = customer.BusinessItem,
            Address = customer.Address,
            DetailAddress = customer.DetailAddress,
            Phone = customer.Phone,
            MobilePhone = customer.MobilePhone,
            FaxNumber = customer.FaxNumber,
            Email = customer.Email,
            HomePage = customer.HomePage,
            Recipient = customer.Recipient,
            PriceGrade = customer.PriceGrade,
            Notes = customer.Notes
        };
    }

    private static bool ContainsStaleOwnerFailure(Exception exception)
    {
        if (exception is StaleCacheOwnerSessionException)
            return true;

        return exception is AggregateException aggregate &&
               aggregate
                   .Flatten()
                   .InnerExceptions
                   .Any(ContainsStaleOwnerFailure);
    }

    private sealed class CustomerContractCacheManifest
    {
        public string Schema { get; set; } = string.Empty;
        public string OwnerHash { get; set; } = string.Empty;
        public Guid CustomerId { get; set; }
        public Guid GenerationId { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public List<CustomerContractDto> Contracts { get; set; } = new();
        public List<CachedPdfBinding> PdfBindings { get; set; } = new();
    }

    private sealed class CachedPdfBinding
    {
        public Guid ContractId { get; set; }
        public long Revision { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string ObjectFileName { get; set; } = string.Empty;

        public CachedPdfBinding Clone()
            => new()
            {
                ContractId = ContractId,
                Revision = Revision,
                Size = Size,
                Sha256 = Sha256,
                ObjectFileName = ObjectFileName
            };
    }

    private sealed class PurgeWatermarkManifest
    {
        public string Schema { get; set; } =
            PurgeWatermarkSchema;
        public Dictionary<Guid, long> CustomerRevisions { get; set; } =
            new();
        public Dictionary<Guid, long> ContractRevisions { get; set; } =
            new();

        public PurgeWatermarkManifest Clone()
            => new()
            {
                Schema = Schema,
                CustomerRevisions =
                    new Dictionary<Guid, long>(CustomerRevisions),
                ContractRevisions =
                    new Dictionary<Guid, long>(ContractRevisions)
            };
    }

    private sealed class CacheScopeManifest
    {
        public string Schema { get; set; } = string.Empty;
        public string OwnerHash { get; set; } = string.Empty;
        public string TenantCode { get; set; } = string.Empty;
        public string OfficeCode { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
    }

    private sealed class RefCountedManifestLock
    {
        public object SyncRoot { get; } = new();
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int ReferenceCount { get; set; }
        public bool Retired { get; set; }
    }

    private sealed class AtomicOwnerMarker
    {
        public string Schema { get; set; } = string.Empty;
        public string TargetFileName { get; set; } = string.Empty;
        public Guid OperationId { get; set; }
    }

    private sealed class AtomicRecoveryEntry
    {
        public string MarkerPath { get; init; } = string.Empty;
        public string TemporaryPath { get; init; } = string.Empty;
        public string BackupPath { get; init; } = string.Empty;
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

public sealed class CacheOwnerSession
{
    internal CacheOwnerSession(
        string tenantCode,
        string officeCode,
        string username,
        string ownerHash,
        string sessionGeneration,
        string rootDirectory,
        bool isExplicitRoot)
    {
        TenantCode = tenantCode;
        OfficeCode = officeCode;
        Username = username;
        OwnerHash = ownerHash;
        SessionGeneration = sessionGeneration;
        RootDirectory = Path.GetFullPath(rootDirectory);
        IsExplicitRoot = isExplicitRoot;
    }

    public string TenantCode { get; }
    public string OfficeCode { get; }
    public string Username { get; }
    public string OwnerHash { get; }
    public string SessionGeneration { get; }
    public string RootDirectory { get; }
    internal bool IsExplicitRoot { get; }

    internal static CacheOwnerSession ForExplicitRoot(
        string rootDirectory)
        => new(
            tenantCode: string.Empty,
            officeCode: string.Empty,
            username: string.Empty,
            ownerHash: "explicit-root",
            sessionGeneration: "explicit-root",
            rootDirectory,
            isExplicitRoot: true);

    internal bool HasSameOwnerAndSession(
        CacheOwnerSession other)
        => string.Equals(
               TenantCode,
               other.TenantCode,
               StringComparison.Ordinal) &&
           string.Equals(
               OfficeCode,
               other.OfficeCode,
               StringComparison.Ordinal) &&
           string.Equals(
               Username,
               other.Username,
               StringComparison.Ordinal) &&
           string.Equals(
               OwnerHash,
               other.OwnerHash,
               StringComparison.Ordinal) &&
           string.Equals(
               SessionGeneration,
               other.SessionGeneration,
               StringComparison.Ordinal) &&
           string.Equals(
               RootDirectory,
               other.RootDirectory,
               StringComparison.OrdinalIgnoreCase);

    public bool HasSameOwnerAndSession(
        MobileSessionOwner other)
        => !IsExplicitRoot &&
           string.Equals(
               TenantCode,
               other.TenantCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               OfficeCode,
               other.OfficeCode,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               Username,
               other.Username,
               StringComparison.OrdinalIgnoreCase) &&
           string.Equals(
               SessionGeneration,
               other.SessionGeneration,
               StringComparison.Ordinal);
}

public sealed class StaleCacheOwnerSessionException :
    InvalidOperationException
{
    public StaleCacheOwnerSessionException(string message)
        : base(message)
    {
    }
}
