using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class CustomerContractCacheStore
{
    private readonly SessionStore _sessionStore;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public CustomerContractCacheStore(SessionStore sessionStore)
    {
        _sessionStore = sessionStore;
    }

    private string RootDirectory => Path.Combine(FileSystem.AppDataDirectory, "contract-cache", BuildScopeKey());
    private string CustomersManifestPath => Path.Combine(RootDirectory, "customers.json");

    public async Task SaveCustomersAsync(IReadOnlyList<CustomerDto> customers, CancellationToken ct = default)
    {
        Directory.CreateDirectory(RootDirectory);

        await using var stream = File.Create(CustomersManifestPath);
        await JsonSerializer.SerializeAsync(
            stream,
            customers.Select(CloneCustomer).ToList(),
            _jsonOptions,
            ct);
    }

    public async Task<IReadOnlyList<CustomerDto>> LoadCustomersAsync(CancellationToken ct = default)
    {
        if (!File.Exists(CustomersManifestPath))
            return Array.Empty<CustomerDto>();

        await using var stream = File.OpenRead(CustomersManifestPath);
        var customers = await JsonSerializer.DeserializeAsync<List<CustomerDto>>(stream, _jsonOptions, ct);
        return customers?.Select(CloneCustomer).ToList() ?? new List<CustomerDto>();
    }

    public async Task SaveContractsAsync(Guid customerId, IReadOnlyList<CustomerContractDto> contracts, CancellationToken ct = default)
    {
        var customerDirectory = GetCustomerDirectory(customerId);
        Directory.CreateDirectory(customerDirectory);

        var manifest = new CustomerContractCacheManifest
        {
            CustomerId = customerId,
            SavedAtUtc = DateTime.UtcNow,
            Contracts = new List<CustomerContractDto>()
        };

        foreach (var contract in contracts)
        {
            if (contract.FileContent is { Length: > 0 } fileContent)
                await File.WriteAllBytesAsync(GetPdfPath(customerId, contract.Id), fileContent, ct);

            manifest.Contracts.Add(CloneWithoutContent(contract));
        }

        await using var stream = File.Create(GetManifestPath(customerId));
        await JsonSerializer.SerializeAsync(stream, manifest, _jsonOptions, ct);
    }

    public async Task<IReadOnlyList<CustomerContractDto>> LoadContractsAsync(Guid customerId, CancellationToken ct = default)
    {
        var manifestPath = GetManifestPath(customerId);
        if (!File.Exists(manifestPath))
            return Array.Empty<CustomerContractDto>();

        await using var stream = File.OpenRead(manifestPath);
        var manifest = await JsonSerializer.DeserializeAsync<CustomerContractCacheManifest>(stream, _jsonOptions, ct);
        manifest ??= new CustomerContractCacheManifest();

        return manifest.Contracts.Select(CloneWithoutContent).ToList();
    }

    public async Task<string?> EnsureCachedPdfAsync(Guid customerId, CustomerContractDto contract, CancellationToken ct = default)
    {
        var pdfPath = GetPdfPath(customerId, contract.Id);
        if (contract.FileContent is { Length: > 0 } fileContent)
        {
            Directory.CreateDirectory(GetCustomerDirectory(customerId));
            await File.WriteAllBytesAsync(pdfPath, fileContent, ct);
            return pdfPath;
        }

        return File.Exists(pdfPath) ? pdfPath : null;
    }

    public async Task<string> CachePdfAsync(Guid customerId, Guid contractId, string sourcePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
            throw new FileNotFoundException("계약서 원본 파일을 찾지 못했습니다.", sourcePath);

        var pdfPath = GetPdfPath(customerId, contractId);
        Directory.CreateDirectory(GetCustomerDirectory(customerId));
        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(pdfPath);
        await source.CopyToAsync(destination, ct);
        await destination.FlushAsync(ct);
        return pdfPath;
    }

    private string GetCustomerDirectory(Guid customerId)
        => Path.Combine(RootDirectory, customerId.ToString("N"));

    private string GetManifestPath(Guid customerId)
        => Path.Combine(GetCustomerDirectory(customerId), "contracts.json");

    private string GetPdfPath(Guid customerId, Guid contractId)
        => Path.Combine(GetCustomerDirectory(customerId), $"{contractId:N}.pdf");

    private string BuildScopeKey()
    {
        var snapshot = _sessionStore.GetSnapshot();
        var tenantCode = string.IsNullOrWhiteSpace(snapshot.TenantCode)
            ? TenantScopeCatalog.UsenetGroup
            : snapshot.TenantCode.Trim().ToUpperInvariant();
        var officeCode = string.IsNullOrWhiteSpace(snapshot.OfficeCode)
            ? OfficeCodeCatalog.Usenet
            : OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(snapshot.OfficeCode, OfficeCodeCatalog.Usenet).ToUpperInvariant();
        var username = string.IsNullOrWhiteSpace(snapshot.Username)
            ? "anonymous"
            : snapshot.Username.Trim().ToLowerInvariant();

        return SanitizePathSegment($"{tenantCode}_{officeCode}_{username}");
    }

    private static string SanitizePathSegment(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? "default" : sanitized;
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

    private sealed class CustomerContractCacheManifest
    {
        public Guid CustomerId { get; set; }
        public DateTime SavedAtUtc { get; set; }
        public List<CustomerContractDto> Contracts { get; set; } = new();
    }
}
