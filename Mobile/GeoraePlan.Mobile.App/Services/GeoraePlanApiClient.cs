using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using Microsoft.Maui.ApplicationModel;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class GeoraePlanApiClient
{
    private static readonly TimeSpan DefaultApiRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FileTransferRequestTimeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http = new();
    private readonly SettingsService _settings;
    private readonly SessionStore _sessionStore;
    private readonly MobileSessionRecoveryService _sessionRecovery;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public GeoraePlanApiClient(
        SettingsService settings,
        SessionStore sessionStore,
        MobileSessionRecoveryService sessionRecovery)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        _sessionRecovery = sessionRecovery;
    }

    public async Task<LoginResponse?> LoginAsync(LoginRequest request, CancellationToken ct = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, BuildUri("auth/login"))
        {
            Content = JsonContent.Create(request, options: _jsonOptions)
        };

        using var response = await SendCoreAsync(() => Task.FromResult(message), ct, DefaultApiRequestTimeout);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return null;

        await EnsureSuccessAsync(response, "auth/login", ct);
        return await response.Content.ReadFromJsonAsync<LoginResponse>(_jsonOptions, ct);
    }

    public async Task<CustomerDto?> GetCustomerByIdAsync(Guid customerId, CancellationToken ct = default)
        => await GetAsync<CustomerDto>($"customers/{customerId}", ct);

    public async Task<CustomerDetailDto?> GetCustomerDetailAsync(Guid customerId, CancellationToken ct = default)
        => await GetAsync<CustomerDetailDto>($"customers/{customerId}/detail", ct);

    public async Task<List<CustomerDto>> GetCustomersAsync(string? searchText, CancellationToken ct = default)
        => await GetAsync<List<CustomerDto>>(BuildQuery("customers", ("q", searchText)), ct) ?? new List<CustomerDto>();

    public Task<CustomerDto?> CreateCustomerAsync(CustomerDto request, CancellationToken ct = default)
        => PostAsync<CustomerDto, CustomerDto>("customers", request, ct);

    public Task<CustomerDto?> UpdateCustomerAsync(CustomerDto request, CancellationToken ct = default)
        => PutAsync<CustomerDto, CustomerDto>($"customers/{request.Id}", request, ct);

    public Task DeleteCustomerAsync(Guid customerId, long? expectedRevision, CancellationToken ct = default)
        => DeleteAsync(BuildQuery("customers/" + customerId, ("expectedRevision", expectedRevision?.ToString())), ct);

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

        if (await IsCachedDownloadValidAsync(cachedPath, contract.FileSize, contract.FileHash, ct))
            return cachedPath;
        TryDeleteFile(cachedPath);

        await DownloadFileToCacheAsync(
            $"customers/contracts/{contract.Id}/content",
            cachedPath,
            contract.FileSize,
            contract.FileHash,
            "계약서 PDF",
            ct);
        return cachedPath;
    }

    public async Task<List<ItemDto>> GetItemsAsync(string? searchText, string? category = null, CancellationToken ct = default)
        => await GetAsync<List<ItemDto>>(BuildQuery("items", ("q", searchText), ("category", category)), ct) ?? new List<ItemDto>();

    public Task<ItemDto?> CreateItemAsync(ItemDto request, CancellationToken ct = default)
        => PostAsync<ItemDto, ItemDto>("items", request, ct);

    public Task<ItemDto?> UpdateItemAsync(ItemDto request, CancellationToken ct = default)
        => PutAsync<ItemDto, ItemDto>($"items/{request.Id}", request, ct);

    public Task DeleteItemAsync(Guid itemId, long? expectedRevision, CancellationToken ct = default)
        => DeleteAsync(BuildQuery("items/" + itemId, ("expectedRevision", expectedRevision?.ToString())), ct);

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

    public async Task<InvoiceDto?> GetInvoiceByIdAsync(Guid invoiceId, CancellationToken ct = default)
        => await GetAsync<InvoiceDto>($"invoices/{invoiceId}", ct);

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

        if (await IsCachedDownloadValidAsync(cachedPath, attachment.FileSize, attachment.FileHash, ct))
            return cachedPath;
        TryDeleteFile(cachedPath);

        if (attachment.FileContent is { Length: > 0 } inlineBytes)
        {
            await WriteBytesToCacheAsync(
                cachedPath,
                inlineBytes,
                attachment.FileSize,
                attachment.FileHash,
                "첨부 파일",
                ct);
            return cachedPath;
        }

        await DownloadFileToCacheAsync(
            $"payments/attachments/{attachment.Id}/content",
            cachedPath,
            attachment.FileSize,
            attachment.FileHash,
            "첨부 파일",
            ct);
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

    public Task<SyncStatusDto?> GetSyncStatusAsync(CancellationToken ct = default)
        => GetAsync<SyncStatusDto>("sync/status", ct);

    public Task<SyncStatusDto?> WaitForSyncChangeAsync(long sinceRevision, TimeSpan timeout, CancellationToken ct = default)
    {
        var timeoutSeconds = Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds), 1, 30);
        return GetAsync<SyncStatusDto>(
            BuildQuery(
                "sync/wait",
                ("sinceRev", Math.Max(0, sinceRevision).ToString()),
                ("timeoutSeconds", timeoutSeconds.ToString())),
            ct,
            requestTimeout: TimeSpan.FromSeconds(timeoutSeconds + 5));
    }

    public Task<AppUpdateManifestDto?> GetUpdateManifestAsync(string channel = "stable", CancellationToken ct = default)
        => GetAsync<AppUpdateManifestDto>($"updates/manifest?channel={Uri.EscapeDataString(channel)}", ct, requireAuthentication: false);

    public Task<IntegrityReportDto?> GetIntegrityReportAsync(CancellationToken ct = default)
        => GetAsync<IntegrityReportDto>("integrity/report", ct);

    public Task<IntegrityIssueDetailResultDto?> GetIntegrityIssueDetailsAsync(string code, CancellationToken ct = default)
        => GetAsync<IntegrityIssueDetailResultDto>(
            BuildQuery("integrity/report/details", ("code", code)),
            ct);

    public Task<InvoiceDto?> CreateInvoiceAsync(InvoiceDto request, CancellationToken ct = default)
        => PostAsync<InvoiceDto, InvoiceDto>("invoices", request, ct);

    public Task<InvoiceDto?> UpdateInvoiceAsync(InvoiceDto request, CancellationToken ct = default)
        => PutAsync<InvoiceDto, InvoiceDto>($"invoices/{request.Id}", request, ct);

    public Task<PaymentDto?> CreatePaymentAsync(PaymentDto request, CancellationToken ct = default)
        => PostAsync<PaymentDto, PaymentDto>("payments", request, ct);

    public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(Guid paymentId, PendingPaymentAttachmentRecord attachment, CancellationToken ct = default)
    {
        using var response = await SendAsync(
            async () =>
            {
                var request = await CreateRequestAsync(HttpMethod.Post, $"payments/{paymentId}/attachments", cancellationToken: ct);
                var form = new MultipartFormDataContent();
                var fileStream = File.OpenRead(attachment.StoredPath);
                var fileContent = new StreamContent(fileStream);
                fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(attachment.MimeType)
                    ? "application/octet-stream"
                    : attachment.MimeType);

                form.Add(fileContent, "file", attachment.FileName);
                form.Add(new StringContent(attachment.AttachmentType ?? "내역첨부"), "attachmentType");
                form.Add(new StringContent(attachment.Description ?? string.Empty), "description");
                if (attachment.LocalId != Guid.Empty)
                    form.Add(new StringContent(attachment.LocalId.ToString("D")), "clientAttachmentId");
                request.Content = form;
                return request;
            },
            $"payments/{paymentId}/attachments",
            ct,
            requestTimeout: FileTransferRequestTimeout);
        await EnsureSuccessAsync(response, $"payments/{paymentId}/attachments", ct);
        return await response.Content.ReadFromJsonAsync<PaymentAttachmentDto>(_jsonOptions, ct);
    }

    private async Task<T?> GetAsync<T>(
        string relative,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null)
    {
        using var response = await SendAsync(
            () => CreateRequestAsync(HttpMethod.Get, relative, requireAuthentication, ct),
            relative,
            ct,
            requireAuthentication,
            requestTimeout);
        await EnsureSuccessAsync(response, relative, ct);
        return await response.Content.ReadFromJsonAsync<T>(_jsonOptions, ct);
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string relative,
        TRequest payload,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null)
    {
        using var response = await SendAsync(
            async () =>
            {
                var request = await CreateRequestAsync(HttpMethod.Post, relative, requireAuthentication, ct);
                request.Content = JsonContent.Create(payload, options: _jsonOptions);
                return request;
            },
            relative,
            ct,
            requireAuthentication,
            requestTimeout);
        await EnsureSuccessAsync(response, relative, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct);
    }

    private async Task<TResponse?> PutAsync<TRequest, TResponse>(
        string relative,
        TRequest payload,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null)
    {
        using var response = await SendAsync(
            async () =>
            {
                var request = await CreateRequestAsync(HttpMethod.Put, relative, requireAuthentication, ct);
                request.Content = JsonContent.Create(payload, options: _jsonOptions);
                return request;
            },
            relative,
            ct,
            requireAuthentication,
            requestTimeout);
        await EnsureSuccessAsync(response, relative, ct);
        return await response.Content.ReadFromJsonAsync<TResponse>(_jsonOptions, ct);
    }

    private async Task DeleteAsync(
        string relative,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null)
    {
        using var response = await SendAsync(
            () => CreateRequestAsync(HttpMethod.Delete, relative, requireAuthentication, ct),
            relative,
            ct,
            requireAuthentication,
            requestTimeout);
        await EnsureSuccessAsync(response, relative, ct);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relative,
        bool requireAuthentication = true,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, BuildUri(relative));
        if (!requireAuthentication)
            return request;

        var token = await GetAccessTokenAsync(relative, cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            await HandleAuthenticationFailureAsync();
            throw new MobileAuthenticationException(relative, $"인증 토큰을 찾지 못해 Authorization 헤더 없이 요청하려고 했습니다. 다시 로그인해 주세요. (요청: {relative})");
        }

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

    private async Task<string?> GetAccessTokenAsync(string relative, CancellationToken ct)
    {
        var token = await _sessionStore.GetTokenAsync(clearStaleSession: false);
        if (!string.IsNullOrWhiteSpace(token))
            return token;

        var recovery = await _sessionRecovery.TryRestoreSessionAsync($"token:{relative}", ct);
        if (recovery.Success)
            return await _sessionStore.GetTokenAsync(clearStaleSession: false);

        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        string relative,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null)
    {
#if DEBUG
        await MobileDiagnosticFaultInjector.ThrowIfConfiguredAsync(relative, ct);
#endif
        var response = await SendCoreAsync(requestFactory, ct, requestTimeout ?? DefaultApiRequestTimeout);
        if (!requireAuthentication || response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();

        var recovery = await _sessionRecovery.TryRestoreSessionAsync($"401:{relative}", forceRefresh: true, ct: ct);
        if (recovery.Success)
        {
            var retryResponse = await SendCoreAsync(requestFactory, ct, requestTimeout ?? DefaultApiRequestTimeout);
            if (retryResponse.StatusCode != HttpStatusCode.Unauthorized)
                return retryResponse;

            retryResponse.Dispose();
        }

        await HandleAuthenticationFailureAsync();
        throw new MobileAuthenticationException(relative,
            $"401 Unauthorized ({relative}): 저장된 Bearer 토큰이 만료되었거나 권한/담당지점/사업 범위가 변경되어 자동 로그인으로도 복구하지 못했습니다. 다시 로그인해 주세요.".Trim());
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        CancellationToken ct,
        TimeSpan requestTimeout)
    {
        using var request = await requestFactory();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(requestTimeout);
        return await _http.SendAsync(request, timeoutCts.Token);
    }

    private async Task EnsureSuccessAsync(HttpResponseMessage response, string relative, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(ct);
        var failureMessage = ApiErrorMessageFormatter.BuildFailureMessage(
            response.StatusCode,
            response.ReasonPhrase,
            body);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleAuthenticationFailureAsync();
            throw new MobileAuthenticationException(relative,
                $"401 Unauthorized ({relative}): 서버가 Bearer 토큰을 거부했습니다. 세션이 만료되었거나 권한/담당지점/사업 범위가 변경되었을 수 있습니다. 다시 로그인해 주세요. {failureMessage}".Trim());
        }

        throw new HttpRequestException(failureMessage, null, response.StatusCode);
    }

    private async Task HandleAuthenticationFailureAsync()
    {
        await _sessionStore.ClearAsync();
        MainThread.BeginInvokeOnMainThread(App.ShowLogin);
    }

    private async Task DownloadFileToCacheAsync(
        string relative,
        string cachedPath,
        long expectedSize,
        string? expectedSha256,
        string label,
        CancellationToken ct)
    {
        var temporaryPath = cachedPath + ".download";
        TryDeleteFile(temporaryPath);

        try
        {
            using var response = await SendAsync(
                () => CreateRequestAsync(HttpMethod.Get, relative, cancellationToken: ct),
                relative,
                ct,
                requestTimeout: FileTransferRequestTimeout);
            await EnsureSuccessAsync(response, relative, ct);

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(temporaryPath))
            {
                await source.CopyToAsync(target, ct);
                await target.FlushAsync(ct);
            }

            await ValidateDownloadedFileAsync(temporaryPath, expectedSize, expectedSha256, label, ct);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
            File.Move(temporaryPath, cachedPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static async Task WriteBytesToCacheAsync(
        string cachedPath,
        byte[] bytes,
        long expectedSize,
        string? expectedSha256,
        string label,
        CancellationToken ct)
    {
        var temporaryPath = cachedPath + ".download";
        TryDeleteFile(temporaryPath);

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
            await ValidateDownloadedFileAsync(temporaryPath, expectedSize, expectedSha256, label, ct);
            File.Move(temporaryPath, cachedPath, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private static async Task<bool> IsCachedDownloadValidAsync(
        string path,
        long expectedSize,
        string? expectedSha256,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            return false;

        var length = new FileInfo(path).Length;
        if (length <= 0)
            return false;

        if (expectedSize > 0 && length != expectedSize)
            return false;

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualSha256 = await ComputeSha256Async(path, ct);
            if (!string.Equals(actualSha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }

    private static async Task ValidateDownloadedFileAsync(
        string path,
        long expectedSize,
        string? expectedSha256,
        string label,
        CancellationToken ct)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"{label} 다운로드 파일을 찾지 못했습니다.", path);

        var length = new FileInfo(path).Length;
        if (length <= 0)
            throw new InvalidDataException($"{label} 다운로드 결과가 비어 있습니다. 다시 시도해 주세요.");

        if (expectedSize > 0 && length != expectedSize)
            throw new InvalidDataException($"{label} 다운로드 크기가 서버 정보와 다릅니다. 다시 시도하거나 관리자에게 문의해 주세요.");

        if (!string.IsNullOrWhiteSpace(expectedSha256))
        {
            var actualSha256 = await ComputeSha256Async(path, ct);
            if (!string.Equals(actualSha256, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"{label} 다운로드 해시가 서버 정보와 다릅니다. 캐시를 삭제하고 다시 내려받아 주세요.");
        }
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
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
            // Best-effort cleanup only. The next download attempt will overwrite temporary files.
        }
    }
}
