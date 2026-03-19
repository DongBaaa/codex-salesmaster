using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
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

    public async Task<List<CustomerDto>> GetCustomersAsync(string? searchText, CancellationToken ct = default)
        => await GetAsync<List<CustomerDto>>(BuildQuery("customers", ("q", searchText)), ct) ?? new List<CustomerDto>();

    public async Task<List<ItemDto>> GetItemsAsync(string? searchText, CancellationToken ct = default)
        => await GetAsync<List<ItemDto>>(BuildQuery("items", ("q", searchText)), ct) ?? new List<ItemDto>();

    public async Task<List<InvoiceDto>> GetInvoicesAsync(string? searchText, CancellationToken ct = default)
        => await GetAsync<List<InvoiceDto>>(BuildQuery("invoices", ("q", searchText), ("take", "100")), ct) ?? new List<InvoiceDto>();

    public Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
        => GetAsync<SyncPullResponse>($"sync/pull?sinceRev={sinceRevision}", ct);

    public Task<SyncPushResult?> PushAsync(SyncPushRequest request, CancellationToken ct = default)
        => PostAsync<SyncPushRequest, SyncPushResult>("sync/push", request, ct);

    public Task<InvoiceDto?> CreateInvoiceAsync(InvoiceDto request, CancellationToken ct = default)
        => PostAsync<InvoiceDto, InvoiceDto>("invoices", request, ct);

    public Task<PaymentDto?> CreatePaymentAsync(PaymentDto request, CancellationToken ct = default)
        => PostAsync<PaymentDto, PaymentDto>("payments", request, ct);

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

    private Uri BuildUri(string relative)
    {
        var baseUrl = _settings.GetBaseUrl();
        return new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), relative.TrimStart('/'));
    }

    private static string BuildQuery(string path, params (string Key, string? Value)[] query)
    {
        var items = query
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")
            .ToList();

        return items.Count == 0 ? path : $"{path}?{string.Join("&", items)}";
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
