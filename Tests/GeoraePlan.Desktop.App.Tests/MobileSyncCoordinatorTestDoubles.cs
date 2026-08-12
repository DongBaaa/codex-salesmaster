using GeoraePlan.Mobile.App.Models;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class JsonSyncStateStore : IDisposable
{
    private readonly string _rootDirectory;
    private readonly string _statePath;
    private readonly JsonSerializerOptions _jsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
    private readonly SessionStore? _sessionStore;
    private readonly Dictionary<string, MobileSyncState>
        _ownerStates =
            new(StringComparer.OrdinalIgnoreCase);

    public JsonSyncStateStore(MobileSyncState state)
    {
        _rootDirectory = Path.Combine(
            Path.GetTempPath(),
            "georaeplan-mobile-sync-state-tests",
            Guid.NewGuid().ToString("N"));
        _statePath = Path.Combine(_rootDirectory, "state.json");
        Directory.CreateDirectory(_rootDirectory);
        SaveStateFileAsync(state, CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }

    public JsonSyncStateStore(
        SessionStore sessionStore,
        MobileSyncState state)
        : this(state)
    {
        _sessionStore = sessionStore;
        var owner = MobileSessionOwner.Capture(
            sessionStore.GetSnapshot());
        _ownerStates[owner.BuildStateKey()] =
            Clone(state);
    }

    public IReadOnlyList<MobileSyncState> SavedStates => _savedStates;
    public IReadOnlyList<MobileSessionOwner> SavedOwners =>
        _savedOwners;
    public Func<
        MobileSessionOwner,
        MobileSyncState,
        Task>? BeforeOwnerSaveAsync { get; set; }

    private readonly List<MobileSyncState> _savedStates = new();
    private readonly List<MobileSessionOwner> _savedOwners =
        new();

    public async Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(_statePath);
        var state = await JsonSerializer.DeserializeAsync<MobileSyncState>(
            stream,
            _jsonOptions,
            ct);
        return state ?? throw new InvalidDataException("Stored mobile sync state is null.");
    }

    public Task<MobileSyncState> LoadAsync(
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        if (_sessionStore is null)
            return LoadAsync(ct);

        _sessionStore.ThrowIfOwnerChanged(owner);
        return Task.FromResult(
            _ownerStates.TryGetValue(
                owner.BuildStateKey(),
                out var state)
                ? Clone(state)
                : NewState(owner));
    }

    public Task<MobileSyncState> LoadForOwnerAsync(
        MobileSessionOwner owner)
        => Task.FromResult(
            _ownerStates.TryGetValue(
                owner.BuildStateKey(),
                out var state)
                ? Clone(state)
                : NewState(owner));

    public async Task SaveAsync(MobileSyncState state, CancellationToken ct = default)
    {
        await SaveStateFileAsync(state, ct);
        _savedStates.Add(await LoadAsync(ct));
    }

    public async Task SaveAsync(
        MobileSessionOwner owner,
        MobileSyncState state,
        CancellationToken ct = default)
    {
        if (_sessionStore is null)
        {
            await SaveAsync(state, ct);
            return;
        }

        _sessionStore.ThrowIfOwnerChanged(owner);
        if (BeforeOwnerSaveAsync is not null)
        {
            await BeforeOwnerSaveAsync(
                owner,
                state);
        }
        _sessionStore.ThrowIfOwnerChanged(owner);
        var clone = Clone(state);
        clone.OwnerUsername = owner.Username;
        clone.OwnerTenantCode = owner.TenantCode;
        clone.OwnerOfficeCode = owner.OfficeCode;
        clone.OwnerSessionGeneration =
            owner.SessionGeneration;
        _ownerStates[owner.BuildStateKey()] = clone;
        _savedStates.Add(Clone(clone));
        _savedOwners.Add(owner);
    }

    private async Task SaveStateFileAsync(
        MobileSyncState state,
        CancellationToken ct)
    {
        state.Normalize();
        var temporaryPath = Path.Combine(
            _rootDirectory,
            $".state.{Guid.NewGuid():N}.tmp");
        try
        {
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
                    state,
                    _jsonOptions,
                    ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_statePath))
                File.Replace(temporaryPath, _statePath, null);
            else
                File.Move(temporaryPath, _statePath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
            Directory.Delete(_rootDirectory, recursive: true);
    }

    private MobileSyncState Clone(MobileSyncState state)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            state,
            _jsonOptions);
        var clone = JsonSerializer.Deserialize<MobileSyncState>(
            bytes,
            _jsonOptions) ??
            throw new InvalidDataException(
                "Stored mobile sync state clone is null.");
        clone.Normalize();
        return clone;
    }

    private static MobileSyncState NewState(
        MobileSessionOwner owner)
    {
        var state = new MobileSyncState
        {
            OwnerUsername = owner.Username,
            OwnerTenantCode = owner.TenantCode,
            OwnerOfficeCode = owner.OfficeCode,
            OwnerSessionGeneration =
                owner.SessionGeneration
        };
        state.Normalize();
        return state;
    }
}

public sealed class GeoraePlanApiClient
{
    private readonly Queue<SyncPullResponse?> _pullResponses;

    public GeoraePlanApiClient(params SyncPullResponse?[] pullResponses)
    {
        _pullResponses = new Queue<SyncPullResponse?>(pullResponses);
    }

    public List<long> RequestedPullRevisions { get; } = new();
    public List<SyncPushRequest> SubmittedPushes { get; } =
        new();
    public Func<Task>? BeforePullReturnAsync { get; set; }
    public Func<Task>? BeforePushReturnAsync { get; set; }
    public Func<Task>? BeforeInvoiceReturnAsync { get; set; }
    public Func<Task>? BeforePaymentReturnAsync { get; set; }
    public Func<
        MobileSessionOwner,
        PendingPaymentAttachmentRecord,
        Task>? BeforePaymentAttachmentUploadAsync { get; set; }
    public SyncPushResult PushResult { get; set; } = new();
    public Exception? PaymentAttachmentUploadException { get; set; }
    public int PaymentAttachmentUploadAttempts { get; private set; }
    public List<MobileSessionOwner> SubmittedInvoiceOwners { get; } =
        [];
    public List<MobileSessionOwner> SubmittedPaymentOwners { get; } =
        [];
    public List<(MobileSessionOwner Owner, Guid LocalId)>
        SubmittedPaymentAttachmentOwners { get; } = [];

    public Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequestedPullRevisions.Add(sinceRevision);
        return Task.FromResult(
            _pullResponses.Count > 0
                ? _pullResponses.Dequeue()
                : null);
    }

    public async Task<SyncPullResponse?> PullAsync(
        long sinceRevision,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RequestedPullRevisions.Add(sinceRevision);
        var response = _pullResponses.Count > 0
            ? _pullResponses.Dequeue()
            : null;
        if (BeforePullReturnAsync is not null)
            await BeforePullReturnAsync();
        return response;
    }

    public Task<SyncPushResult?> PushAsync(
        SyncPushRequest request,
        CancellationToken ct = default)
        => Task.FromResult<SyncPushResult?>(new SyncPushResult());

    public async Task<SyncPushResult?> PushAsync(
        SyncPushRequest request,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedPushes.Add(request);
        if (BeforePushReturnAsync is not null)
            await BeforePushReturnAsync();
        return PushResult;
    }

    public Task<SyncStatusDto?> GetSyncStatusAsync(CancellationToken ct = default)
        => Task.FromResult<SyncStatusDto?>(new SyncStatusDto());

    public Task<SyncStatusDto?> GetSyncStatusAsync(
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => GetSyncStatusAsync(ct);

    public Task<InvoiceDto?> CreateInvoiceAsync(
        InvoiceDto invoice,
        CancellationToken ct = default)
        => Task.FromResult<InvoiceDto?>(invoice);

    public Task<InvoiceDto?> CreateInvoiceAsync(
        InvoiceDto invoice,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => ReturnInvoiceAsync(
            invoice,
            owner,
            ct);

    public Task<InvoiceDto?> UpdateInvoiceAsync(
        InvoiceDto invoice,
        CancellationToken ct = default)
        => Task.FromResult<InvoiceDto?>(invoice);

    public Task<InvoiceDto?> UpdateInvoiceAsync(
        InvoiceDto invoice,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => ReturnInvoiceAsync(
            invoice,
            owner,
            ct);

    public Task<PaymentDto?> CreatePaymentAsync(
        PaymentDto payment,
        CancellationToken ct = default)
        => Task.FromResult<PaymentDto?>(payment);

    public Task<PaymentDto?> CreatePaymentAsync(
        PaymentDto payment,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => ReturnPaymentAsync(
            payment,
            owner,
            ct);

    private async Task<PaymentDto?> ReturnPaymentAsync(
        PaymentDto payment,
        MobileSessionOwner owner,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedPaymentOwners.Add(owner);
        if (BeforePaymentReturnAsync is not null)
            await BeforePaymentReturnAsync();
        return payment;
    }

    private async Task<InvoiceDto?> ReturnInvoiceAsync(
        InvoiceDto invoice,
        MobileSessionOwner owner,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedInvoiceOwners.Add(owner);
        if (BeforeInvoiceReturnAsync is not null)
            await BeforeInvoiceReturnAsync();
        return invoice;
    }

    public Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(
        Guid paymentId,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        PaymentAttachmentUploadAttempts++;
        return PaymentAttachmentUploadException is null
            ? Task.FromResult<PaymentAttachmentDto?>(
                new PaymentAttachmentDto())
            : Task.FromException<PaymentAttachmentDto?>(
                PaymentAttachmentUploadException);
    }

    public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(
        Guid paymentId,
        PendingPaymentAttachmentRecord attachment,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        SubmittedPaymentAttachmentOwners.Add((
            owner,
            attachment.LocalId));
        if (BeforePaymentAttachmentUploadAsync is not null)
        {
            await BeforePaymentAttachmentUploadAsync(
                owner,
                attachment);
        }

        return await UploadPaymentAttachmentAsync(
            paymentId,
            attachment,
            ct);
    }
}

public sealed class PaymentAttachmentDraftStore
{
    public Func<
        MobileSessionOwner,
        PendingPaymentAttachmentRecord,
        Task>? BeforeRemoveAsync { get; set; }

    public List<(
        MobileSessionOwner Owner,
        Guid LocalId)> RemovedDrafts { get; } = [];

    public List<(
        MobileSessionOwner Owner,
        Guid LocalId)> RemovalAttempts { get; } = [];

    public List<MobileSessionOwner>
        OrphanCleanupOwners { get; } = [];

    public Task<bool> PrepareOwnedDraftsAsync(
        MobileSessionOwner owner,
        IEnumerable<PendingPaymentAttachmentRecord>? attachments,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public Task<string?> ResolveOwnedPathAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<string?>(
            attachment.StoredPath);
    }

    public async Task RemoveAsync(
        MobileSessionOwner owner,
        PendingPaymentAttachmentRecord attachment,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        RemovalAttempts.Add((
            owner,
            attachment.LocalId));
        if (BeforeRemoveAsync is not null)
        {
            await BeforeRemoveAsync(
                owner,
                attachment);
        }

        RemovedDrafts.Add((
            owner,
            attachment.LocalId));
    }

    public Task<int> RemoveOrphanDraftsAsync(
        MobileSessionOwner owner,
        IEnumerable<PendingPaymentAttachmentRecord> retainedAttachments,
        TimeSpan minimumAge,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        OrphanCleanupOwners.Add(owner);
        return Task.FromResult(0);
    }
}

public sealed class SessionStore
{
    private readonly SemaphoreSlim _ownerMutationGate =
        new(1, 1);

    public SessionSnapshot Snapshot { get; set; } = new();

    public SessionSnapshot GetSnapshot() => Snapshot;

    public MobileSessionOwner CaptureOwner()
        => MobileSessionOwner.Capture(Snapshot);

    public bool IsOwnerCurrent(MobileSessionOwner owner)
        => owner.Matches(Snapshot);

    public void ThrowIfOwnerChanged(MobileSessionOwner owner)
    {
        if (!IsOwnerCurrent(owner))
        {
            throw new StaleMobileSessionOwnerException(
                "The test mobile owner changed.");
        }
    }

    public async Task<IDisposable> AcquireOwnerCommitLeaseAsync(
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        await _ownerMutationGate.WaitAsync(ct);
        try
        {
            ThrowIfOwnerChanged(owner);
            return new OwnerCommitLease(_ownerMutationGate);
        }
        catch
        {
            _ownerMutationGate.Release();
            throw;
        }
    }

    public async Task ReplaceSnapshotAsync(
        SessionSnapshot snapshot,
        CancellationToken ct = default)
    {
        await _ownerMutationGate.WaitAsync(ct);
        try
        {
            Snapshot = snapshot;
        }
        finally
        {
            _ownerMutationGate.Release();
        }
    }

    private sealed class OwnerCommitLease : IDisposable
    {
        private SemaphoreSlim? _gate;

        public OwnerCommitLease(SemaphoreSlim gate)
        {
            _gate = gate;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}

public sealed class SessionSnapshot
{
    public bool IsAuthenticated { get; set; } = true;
    public string Username { get; set; } = "cursor-test";
    public string Role { get; set; } = string.Empty;
    public string TenantCode { get; set; } = TenantScopeCatalog.UsenetGroup;
    public string OfficeCode { get; set; } = OfficeCodeCatalog.Usenet;
    public string SessionGeneration { get; set; } = "cursor-test-session";
    public string ScopeType { get; set; } =
        TenantScopeCatalog.ScopeOfficeOnly;
    public bool IsAdmin =>
        string.Equals(
            Role,
            "Admin",
            StringComparison.OrdinalIgnoreCase);
}

public sealed record MobileSessionOwner
{
    private MobileSessionOwner(
        bool isAuthenticated,
        string username,
        string tenantCode,
        string officeCode,
        string sessionGeneration)
    {
        IsAuthenticated = isAuthenticated;
        Username = username;
        TenantCode = tenantCode;
        OfficeCode = officeCode;
        SessionGeneration = sessionGeneration;
    }

    public bool IsAuthenticated { get; }
    public string Username { get; }
    public string TenantCode { get; }
    public string OfficeCode { get; }
    public string SessionGeneration { get; }

    public static MobileSessionOwner Capture(
        SessionSnapshot snapshot)
        => new(
            snapshot.IsAuthenticated,
            snapshot.Username?.Trim() ?? string.Empty,
            snapshot.TenantCode?.Trim() ??
            string.Empty,
            snapshot.OfficeCode?.Trim() ??
            string.Empty,
            snapshot.SessionGeneration?.Trim() ??
            string.Empty);

    public bool Matches(SessionSnapshot snapshot)
    {
        var other = Capture(snapshot);
        return IsAuthenticated == other.IsAuthenticated &&
               string.Equals(
                   Username,
                   other.Username,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   TenantCode,
                   other.TenantCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   OfficeCode,
                   other.OfficeCode,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   SessionGeneration,
                   other.SessionGeneration,
                   StringComparison.Ordinal);
    }

    public string BuildStateKey()
        => $"{Username}|{TenantCode}|{OfficeCode}"
            .ToUpperInvariant();
}

public sealed class StaleMobileSessionOwnerException :
    InvalidOperationException
{
    public StaleMobileSessionOwnerException(string message)
        : base(message)
    {
    }
}

public static class MobilePendingScopeFilter
{
    public static SyncPushRequest CreateScopedPushRequest(
        SessionSnapshot session,
        MobileSyncState state)
        => state.PendingPush;

    public static bool HasScopedServerSyncPayload(
        SessionSnapshot session,
        MobileSyncState state)
        => false;

    public static IReadOnlyList<PendingPaymentAttachmentRecord>
        GetScopedPaymentAttachments(
            SessionSnapshot session,
            MobileSyncState state,
            IReadOnlySet<Guid>? additionalAllowedPaymentIds = null)
        => state.PendingPaymentAttachments;
}

public static class MobileRetryableNetworkFailure
{
    public static bool IsRetryable(Exception exception) => false;
}

public sealed class MobileAuthenticationException : Exception
{
}

public static class ApiConflictReasonTranslator
{
    public static string ToUserMessage(string? reason) => reason ?? string.Empty;
}

public static class MobileAppLogger
{
    public static void Warn(string category, string message)
    {
    }
}

public static class FileSystem
{
    public static string AppDataDirectory =>
        throw new InvalidOperationException(
            "Tests must use the explicit CustomerContractCacheStore root.");
}
