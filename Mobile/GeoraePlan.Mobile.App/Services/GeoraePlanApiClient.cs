using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class GeoraePlanApiClient
{
    private readonly HttpClient _http = new();
    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public GeoraePlanApiClient(SettingsService settings, SessionStore sessionStore)
    {
        _settings = settings;
        _sessionStore = sessionStore;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri("auth/login"))
        {
            Content = JsonContent.Create(request, options: _jsonOptions)
        };

        using var response = await _http.SendAsync(message, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions, ct);
    }

    public async Task<CustomerDto?> GetCustomerByIdAsync(Guid customerId, CancellationToken ct = default)
        => await GetAsync<CustomerDto>($"customers/{customerId}", ct);

    public async Task<CustomerDetailDto?> GetCustomerDetailAsync(Guid customerId, CancellationToken ct = default)
        => await GetAsync<CustomerDetailDto>($"customers/{customerId}/detail", ct);

    public async Task<List<CustomerDto>> GetCustomersAsync(string? searchText, CancellationToken ct = default)
        => await GetAsync<List<CustomerDto>>(BuildQuery("customers", ("q", searchText)), ct) ?? new List<CustomerDto>();

    public async Task<List<CustomerContractDto>> GetCustomerContractsAsync(Guid customerId, CancellationToken ct = default)
        => await GetAsync<List<CustomerContractDto>>($"customers/{customerId}/contracts", ct) ?? new List<CustomerContractDto>();

    public async Task<string> DownloadCustomerContractAsync(CustomerContractDto contract, CancellationToken ct = default)
    {
        var cacheRoot = Path.Combine(FileSystem.CacheDirectory, "customer-contracts");
        Directory.CreateDirectory(cacheRoot);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(contract.FileName)
            ? $"customer-contract-{contract.Id:N}.pdf"
            : contract.FileName);
        var cachedPath = Path.Combine(cacheRoot, $"{contract.Id:N}_{safeName}");

        if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 0)
            return cachedPath;

        using var request = await CreateRequestAsync(HttpMethod.Get, $"customers/contracts/{contract.Id}/content");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(cachedPath);
        await source.CopyToAsync(target, ct);
        await target.FlushAsync(ct);
        return cachedPath;
    }

    public async Task<List<ItemDto>> GetItemsAsync(string? searchText, string? category = null, CancellationToken ct = default)
        => await GetAsync<List<ItemDto>>(BuildQuery("items", ("q", searchText), ("category", category)), ct) ?? new List<ItemDto>();

    public async Task<List<ItemCategorySummaryDto>> GetItemCategoriesAsync(CancellationToken ct = default)
        => await GetAsync<List<ItemCategorySummaryDto>>("items/categories", ct) ?? new List<ItemCategorySummaryDto>();

    public async Task<ItemDetailDto?> GetItemDetailAsync(Guid itemId, CancellationToken ct = default)
        => await GetAsync<ItemDetailDto>($"items/{itemId}/detail", ct);

    public async Task<List<InvoiceDto>> GetInvoicesAsync(string? searchText, Guid? customerId = null, int take = 100, CancellationToken ct = default)
        => await GetAsync<List<InvoiceDto>>(BuildQuery(
                "invoices",
                ("q", searchText),
                ("customerId", customerId?.ToString()),
                ("take", take.ToString())),
            ct) ?? new List<InvoiceDto>();

    public async Task<List<PaymentAttachmentDto>> GetPaymentAttachmentsAsync(Guid paymentId, CancellationToken ct = default)
        => await GetAsync<List<PaymentAttachmentDto>>($"payments/{paymentId}/attachments", ct) ?? new List<PaymentAttachmentDto>();

    public async Task<string> DownloadPaymentAttachmentAsync(PaymentAttachmentDto attachment, CancellationToken ct = default)
    {
        var cacheRoot = Path.Combine(FileSystem.CacheDirectory, "payment-attachments");
        Directory.CreateDirectory(cacheRoot);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(attachment.FileName)
            ? $"payment-attachment-{attachment.Id:N}"
            : attachment.FileName);
        var cachedPath = Path.Combine(cacheRoot, $"{attachment.Id:N}_{safeName}");

        if (File.Exists(cachedPath) && new FileInfo(cachedPath).Length > 0)
            return cachedPath;

        if (attachment.FileContent is { Length: > 0 } inlineBytes)
        {
            await File.WriteAllBytesAsync(cachedPath, inlineBytes, ct);
            return cachedPath;
        }

        using var request = await CreateRequestAsync(HttpMethod.Get, $"payments/attachments/{attachment.Id}/content");
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);

        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var target = File.Create(cachedPath);
        await source.CopyToAsync(target, ct);
        await target.FlushAsync(ct);
        return cachedPath;
    }

    public Task<RecycleBinMutationResultDto?> RestoreRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
        => PostAsync<RecycleBinMutationRequest, RecycleBinMutationResultDto>(
            "recycle-bin/restore",
            new RecycleBinMutationRequest { Items = items.ToList() },
            ct);

    public Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
        => PostAsync<RecycleBinMutationRequest, RecycleBinMutationResultDto>(
            "recycle-bin/purge",
            new RecycleBinMutationRequest { Items = items.ToList() },
            ct);

    public async Task<List<RecycleBinEntryDto>> GetRecycleBinAsync(
        string? kind = null,
        string? searchText = null,
        CancellationToken ct = default)
        => await GetAsync<List<RecycleBinEntryDto>>(BuildQuery("recycle-bin", ("kind", kind), ("q", searchText)), ct) ?? new List<RecycleBinEntryDto>();

    public Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
        => GetAsync<SyncPullResponse>($"sync/pull?sinceRev={sinceRevision}", ct);

    public Task<SyncPushResult?> PushAsync(SyncPushRequest request, CancellationToken ct = default)
        => PostAsync<SyncPushRequest, SyncPushResult>("sync/push", request, ct);

    public Task<AppUpdateManifestDto?> GetUpdateManifestAsync(string channel = "stable", CancellationToken ct = default)
        => GetAsync<AppUpdateManifestDto>($"updates/manifest?channel={Uri.EscapeDataString(channel)}", ct);

    public Task<InvoiceDto?> CreateInvoiceAsync(InvoiceDto request, CancellationToken ct = default)
        => PostAsync<InvoiceDto, InvoiceDto>("invoices", request, ct);

    public Task<PaymentDto?> CreatePaymentAsync(PaymentDto request, CancellationToken ct = default)
        => PostAsync<PaymentDto, PaymentDto>("payments", request, ct);

    public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(Guid paymentId, PendingPaymentAttachmentRecord attachment, CancellationToken ct = default)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, $"payments/{paymentId}/attachments");
        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(attachment.StoredPath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(attachment.MimeType)
            ? "application/octet-stream"
            : attachment.MimeType);

        form.Add(fileContent, "file", attachment.FileName);
        form.Add(new StringContent(attachment.AttachmentType ?? "내역첨부"), "attachmentType");
        form.Add(new StringContent(attachment.Description ?? string.Empty), "description");
        request.Content = form;

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<PaymentAttachmentDto>(_jsonOptions, ct);
    }

    private async Task<T?> GetAsync<T>(string relative, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, relative);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct);
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(string relative, TRequest payload, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, relative);
        request.Content = JsonContent.Create(payload, options: _jsonOptions);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string relative)
    {
        var request = new HttpRequestMessage(method, BuildUri(relative));
        var token = await _sessionStore.GetTokenAsync();
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return request;
    }

    public string ResolveAbsoluteUrl(string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return string.Empty;

        var normalized = relativeOrAbsolute.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var absoluteUri) &&
            (string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(absoluteUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return absoluteUri.ToString();
        }

        return BuildUri(normalized.TrimStart('/')).ToString();
    }

    public Uri GetBaseUri()
    {
        var baseUrl = _settings.GetBaseUrl();
        return new Uri(baseUrl.TrimEnd('/') + "/");
    }

    private Uri BuildUri(string relative)
    {
        return new Uri(GetBaseUri(), relative.TrimStart('/'));
    }

    private static string BuildQuery(string path, params (string Key, string? Value)[] query)
    {
        var items = query
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")
            .ToList();

        return items.Count == 0 ? path : $"{path}?{string.Join("&", items)}";
    }

    private static string SanitizeFileName(string fileName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join("_", fileName.Split(invalid, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "attachment.bin" : sanitized;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 200)
            body = body[..200] + "...";

        throw new HttpRequestException($"{(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim(), null, response.StatusCode);
    }
}
