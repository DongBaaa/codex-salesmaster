using 거래플랜.Shared.Contracts;

// Customer transport/cache IO and platform file-launch plumbing are replaced.
// Permission decisions and view-model publication run in production sources.
namespace Microsoft.Maui.ApplicationModel
{
    public sealed class ReadOnlyFile(string path) { public string Path { get; } = path; }
    public sealed class OpenFileRequest(string name, ReadOnlyFile file)
    { public string Name { get; } = name; public ReadOnlyFile File { get; } = file; }
    public sealed class Launcher
    {
        public static Launcher Default { get; } = new();
        public Task<bool> OpenAsync(OpenFileRequest request) => throw new NotSupportedException();
    }
}
namespace GeoraePlan.Mobile.App.Services
{
    public sealed class CacheOwnerSession(MobileSessionOwner owner)
    {
        public MobileSessionOwner Owner { get; } = owner;
        public bool HasSameOwnerAndSession(CacheOwnerSession other) => Owner == other.Owner;
        public bool HasSameOwnerAndSession(MobileSessionOwner other) => Owner == other;
    }
    public sealed class StaleCacheOwnerSessionException(string message) : InvalidOperationException(message) { }
    public sealed class CustomerContractCacheStore(SessionStore session)
    {
        private IReadOnlyList<CustomerDto> _customers = [];
        private IReadOnlyList<CustomerContractDto> _contracts = [];
        public CacheOwnerSession CaptureOwnerSession() => new(session.CaptureOwner());
        public bool IsOwnerSessionCurrent(CacheOwnerSession owner) => session.IsOwnerCurrent(owner.Owner);
        public void ThrowIfOwnerSessionStale(CacheOwnerSession owner)
        {
            if (!IsOwnerSessionCurrent(owner)) throw new StaleCacheOwnerSessionException("stale test cache owner");
        }
        public Task SaveCustomersAsync(CacheOwnerSession owner, IReadOnlyList<CustomerDto> rows)
        { ThrowIfOwnerSessionStale(owner); _customers = rows; return Task.CompletedTask; }
        public Task<IReadOnlyList<CustomerDto>> LoadCustomersAsync(CacheOwnerSession? owner = null)
        { if (owner is not null) ThrowIfOwnerSessionStale(owner); return Task.FromResult(_customers); }
        public Task SaveContractsAsync(CacheOwnerSession owner, Guid id, IReadOnlyList<CustomerContractDto> rows)
        { ThrowIfOwnerSessionStale(owner); _contracts = rows; return Task.CompletedTask; }
        public Task<IReadOnlyList<CustomerContractDto>> LoadContractsAsync(CacheOwnerSession owner, Guid id)
        { ThrowIfOwnerSessionStale(owner); return Task.FromResult(_contracts); }
        public Task RemoveCustomerFromIndexAsync(CacheOwnerSession owner, Guid id)
        { ThrowIfOwnerSessionStale(owner); _customers = _customers.Where(x => x.Id != id).ToArray(); return Task.CompletedTask; }
        public Task<string?> EnsureCachedPdfAsync(CacheOwnerSession owner, Guid id, CustomerContractDto contract)
            => throw new NotSupportedException();
    }
    public sealed class GeoraePlanApiClient
    {
        public CustomerDetailDto Detail { get; set; } = new();
        public bool Offline { get; set; }
        public Func<Task>? BeforeDetailReturn { get; set; }
        public Task<IReadOnlyList<CustomerDto>> GetCustomersAsync(string search, MobileSessionOwner owner)
            => Task.FromResult<IReadOnlyList<CustomerDto>>([Detail.Customer]);
        public async Task<CustomerDetailDto> GetCustomerDetailAsync(Guid id, MobileSessionOwner owner)
        {
            if (BeforeDetailReturn is not null) await BeforeDetailReturn();
            if (Offline) throw new HttpRequestException("synthetic offline");
            return Detail;
        }
        public Task<IReadOnlyList<CustomerContractDto>> GetCustomerContractsAsync(Guid id, MobileSessionOwner owner)
            => Task.FromResult<IReadOnlyList<CustomerContractDto>>([]);
    }
}
