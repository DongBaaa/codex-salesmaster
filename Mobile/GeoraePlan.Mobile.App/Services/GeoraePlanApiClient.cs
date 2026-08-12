using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
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
    private readonly MobileClientIdentityProvider _clientIdentity;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);

    public GeoraePlanApiClient(
        SettingsService settings,
        SessionStore sessionStore,
        MobileSessionRecoveryService sessionRecovery,
        MobileClientIdentityProvider clientIdentity)
    {
        _settings = settings;
        _sessionStore = sessionStore;
        _sessionRecovery = sessionRecovery;
        _clientIdentity = clientIdentity;
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

    public async Task<CustomerDto?> GetCustomerByIdAsync(
        Guid customerId,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<CustomerDto>(
            $"customers/{customerId}",
            ct,
            expectedOwner: owner);

    public async Task<CustomerDetailDto?> GetCustomerDetailAsync(Guid customerId, CancellationToken ct = default)
        => await GetAsync<CustomerDetailDto>($"customers/{customerId}/detail", ct);

    public async Task<CustomerDetailDto?> GetCustomerDetailAsync(
        Guid customerId,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<CustomerDetailDto>(
            $"customers/{customerId}/detail",
            ct,
            expectedOwner: owner);

    public async Task<List<CustomerDto>> GetCustomersAsync(string? searchText, CancellationToken ct = default)
        => await GetAsync<List<CustomerDto>>(BuildQuery("customers", ("q", searchText)), ct) ?? new List<CustomerDto>();

    public async Task<List<CustomerDto>> GetCustomersAsync(
        string? searchText,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<List<CustomerDto>>(
               BuildQuery("customers", ("q", searchText)),
               ct,
               expectedOwner: owner) ??
           new List<CustomerDto>();

    public Task<CustomerDto?> CreateCustomerAsync(CustomerDto request, CancellationToken ct = default)
        => PostAsync<CustomerDto, CustomerDto>("customers", request, ct);
    public Task<CustomerDto?> CreateCustomerAsync(CustomerDto request, MobileSessionOwner owner, CancellationToken ct = default)
        => PostAsync<CustomerDto, CustomerDto>("customers", request, ct, expectedOwner: owner);

    public Task<CustomerDto?> UpdateCustomerAsync(CustomerDto request, CancellationToken ct = default)
        => PutAsync<CustomerDto, CustomerDto>($"customers/{request.Id}", request, ct);
    public Task<CustomerDto?> UpdateCustomerAsync(CustomerDto request, MobileSessionOwner owner, CancellationToken ct = default)
        => PutAsync<CustomerDto, CustomerDto>($"customers/{request.Id}", request, ct, expectedOwner: owner);

    public Task DeleteCustomerAsync(Guid customerId, long? expectedRevision, CancellationToken ct = default)
        => DeleteAsync(BuildQuery("customers/" + customerId, ("expectedRevision", expectedRevision?.ToString())), ct);
    public Task DeleteCustomerAsync(Guid customerId, long? expectedRevision, MobileSessionOwner owner, CancellationToken ct = default)
        => DeleteAsync(BuildQuery("customers/" + customerId, ("expectedRevision", expectedRevision?.ToString())), ct, expectedOwner: owner);

    public async Task<List<CustomerContractDto>> GetCustomerContractsAsync(Guid customerId, CancellationToken ct = default)
        => await GetCustomerContractsAsync(
            customerId,
            _sessionStore.CaptureOwner(),
            ct);

    public async Task<List<CustomerContractDto>>
        GetCustomerContractsAsync(
            Guid customerId,
            MobileSessionOwner owner,
            CancellationToken ct = default)
        => await GetAsync<List<CustomerContractDto>>(
               $"customers/{customerId}/contracts",
               ct,
               expectedOwner: owner) ??
           new List<CustomerContractDto>();

    public Task<string> DownloadCustomerContractAsync(
        CustomerContractDto contract,
        CancellationToken ct = default)
        => DownloadCustomerContractAsync(
            contract,
            _sessionStore.CaptureOwner(),
            ct);

    public async Task<string> DownloadCustomerContractAsync(
        CustomerContractDto contract,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(owner);
        _sessionStore.ThrowIfOwnerChanged(owner);

        var cacheRoot = ResolveAuthenticatedDownloadCacheRoot(
            owner,
            "customer-contracts");
        Directory.CreateDirectory(cacheRoot);
        _sessionStore.ThrowIfOwnerChanged(owner);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(contract.FileName)
            ? $"customer-contract-{contract.Id:N}.pdf"
            : contract.FileName);
        var cachedPath = Path.Combine(
            cacheRoot,
            $"{contract.Id:N}_r{contract.Revision}_{safeName}");
        await CleanupCachedDownloadTemporaryFilesAsync(
            cachedPath,
            owner,
            ct);

        if (await IsCachedDownloadValidAsync(cachedPath, contract.FileSize, contract.FileHash, ct))
        {
            _sessionStore.ThrowIfOwnerChanged(owner);
            return cachedPath;
        }
        await DeleteCachedDownloadIfCurrentOwnerAsync(
            cachedPath,
            owner,
            ct);

        await DownloadFileToCacheAsync(
            $"customers/contracts/{contract.Id}/content",
            cachedPath,
            contract.FileSize,
            contract.FileHash,
            "계약서 PDF",
            owner,
            ct);
        _sessionStore.ThrowIfOwnerChanged(owner);
        return cachedPath;
    }

    public async Task<List<ItemDto>> GetItemsAsync(string? searchText, string? category = null, CancellationToken ct = default)
        => await GetAsync<List<ItemDto>>(BuildQuery("items", ("q", searchText), ("category", category)), ct) ?? new List<ItemDto>();

    public Task<ItemDto?> CreateItemAsync(ItemDto request, CancellationToken ct = default)
        => PostAsync<ItemDto, ItemDto>("items", request, ct);
    public Task<ItemDto?> CreateItemAsync(ItemDto request, MobileSessionOwner owner, CancellationToken ct = default)
        => PostAsync<ItemDto, ItemDto>("items", request, ct, expectedOwner: owner);

    public Task<ItemDto?> UpdateItemAsync(ItemDto request, CancellationToken ct = default)
        => PutAsync<ItemDto, ItemDto>($"items/{request.Id}", request, ct);
    public Task<ItemDto?> UpdateItemAsync(ItemDto request, MobileSessionOwner owner, CancellationToken ct = default)
        => PutAsync<ItemDto, ItemDto>($"items/{request.Id}", request, ct, expectedOwner: owner);

    public Task DeleteItemAsync(Guid itemId, long? expectedRevision, CancellationToken ct = default)
        => DeleteAsync(BuildQuery("items/" + itemId, ("expectedRevision", expectedRevision?.ToString())), ct);
    public Task DeleteItemAsync(Guid itemId, long? expectedRevision, MobileSessionOwner owner, CancellationToken ct = default)
        => DeleteAsync(BuildQuery("items/" + itemId, ("expectedRevision", expectedRevision?.ToString())), ct, expectedOwner: owner);

    public async Task<List<ItemCategorySummaryDto>> GetItemCategoriesAsync(CancellationToken ct = default)
        => await GetAsync<List<ItemCategorySummaryDto>>("items/categories", ct) ?? new List<ItemCategorySummaryDto>();

    public async Task<ItemDetailDto?> GetItemDetailAsync(Guid itemId, CancellationToken ct = default)
        => await GetAsync<ItemDetailDto>($"items/{itemId}/detail", ct);

    public async Task<ItemDetailDto?> GetItemDetailAsync(
        Guid itemId,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<ItemDetailDto>(
            $"items/{itemId}/detail",
            ct,
            expectedOwner: owner);

    public async Task<List<InvoiceDto>> GetInvoicesAsync(string? searchText, Guid? customerId = null, int take = 100, CancellationToken ct = default)
        => await GetAsync<List<InvoiceDto>>(BuildQuery(
                "invoices",
                ("q", searchText),
                ("customerId", customerId?.ToString()),
                ("take", take.ToString())),
            ct) ?? new List<InvoiceDto>();

    public async Task<List<InvoiceDto>> GetInvoicesAsync(
        string? searchText,
        MobileSessionOwner owner,
        Guid? customerId = null,
        int take = 100,
        CancellationToken ct = default)
        => await GetAsync<List<InvoiceDto>>(
                BuildQuery(
                    "invoices",
                    ("q", searchText),
                    ("customerId", customerId?.ToString()),
                    ("take", take.ToString())),
                ct,
                expectedOwner: owner) ??
            new List<InvoiceDto>();

    public async Task<InvoiceDto?> GetInvoiceByIdAsync(Guid invoiceId, CancellationToken ct = default)
        => await GetAsync<InvoiceDto>($"invoices/{invoiceId}", ct);

    public async Task<InvoiceDto?> GetInvoiceByIdAsync(
        Guid invoiceId,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<InvoiceDto>(
            $"invoices/{invoiceId}",
            ct,
            expectedOwner: owner);

    public async Task<List<PaymentAttachmentDto>> GetPaymentAttachmentsAsync(Guid paymentId, CancellationToken ct = default)
        => await GetPaymentAttachmentsAsync(
            paymentId,
            _sessionStore.CaptureOwner(),
            ct);

    public async Task<List<PaymentAttachmentDto>> GetPaymentAttachmentsAsync(
        Guid paymentId,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<List<PaymentAttachmentDto>>(
               $"payments/{paymentId}/attachments",
               ct,
               expectedOwner: owner) ??
           new List<PaymentAttachmentDto>();

    public Task<string> DownloadPaymentAttachmentAsync(
        PaymentAttachmentDto attachment,
        CancellationToken ct = default)
        => DownloadPaymentAttachmentAsync(
            attachment,
            _sessionStore.CaptureOwner(),
            ct);

    public async Task<string> DownloadPaymentAttachmentAsync(
        PaymentAttachmentDto attachment,
        MobileSessionOwner owner,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentNullException.ThrowIfNull(owner);
        _sessionStore.ThrowIfOwnerChanged(owner);

        var cacheRoot = ResolveAuthenticatedDownloadCacheRoot(
            owner,
            "payment-attachments");
        Directory.CreateDirectory(cacheRoot);
        _sessionStore.ThrowIfOwnerChanged(owner);

        var safeName = SanitizeFileName(string.IsNullOrWhiteSpace(attachment.FileName)
            ? $"payment-attachment-{attachment.Id:N}"
            : attachment.FileName);
        var cachedPath = Path.Combine(
            cacheRoot,
            $"{attachment.Id:N}_r{attachment.Revision}_{safeName}");
        await CleanupCachedDownloadTemporaryFilesAsync(
            cachedPath,
            owner,
            ct);

        if (await IsCachedDownloadValidAsync(cachedPath, attachment.FileSize, attachment.FileHash, ct))
        {
            _sessionStore.ThrowIfOwnerChanged(owner);
            return cachedPath;
        }
        await DeleteCachedDownloadIfCurrentOwnerAsync(
            cachedPath,
            owner,
            ct);

        if (attachment.FileContent is { Length: > 0 } inlineBytes)
        {
            await WriteBytesToCacheAsync(
                cachedPath,
                inlineBytes,
                attachment.FileSize,
                attachment.FileHash,
                "첨부 파일",
                owner,
                ct);
            _sessionStore.ThrowIfOwnerChanged(owner);
            return cachedPath;
        }

        await DownloadFileToCacheAsync(
            $"payments/attachments/{attachment.Id}/content",
            cachedPath,
            attachment.FileSize,
            attachment.FileHash,
            "첨부 파일",
            owner,
            ct);
        _sessionStore.ThrowIfOwnerChanged(owner);
        return cachedPath;
    }

    public Task<RecycleBinMutationResultDto?> RestoreRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
        => PostAsync<RecycleBinMutationRequest, RecycleBinMutationResultDto>(
            "recycle-bin/restore",
            new RecycleBinMutationRequest { Items = items.ToList() },
            ct);

    public Task<RecycleBinMutationResultDto?> RestoreRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => PostAsync<RecycleBinMutationRequest, RecycleBinMutationResultDto>(
            "recycle-bin/restore",
            new RecycleBinMutationRequest { Items = items.ToList() },
            ct,
            expectedOwner: owner);

    public Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
        => PostAsync<RecycleBinMutationRequest, RecycleBinMutationResultDto>(
            "recycle-bin/purge",
            new RecycleBinMutationRequest { Items = items.ToList() },
            ct);

    public Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => PostAsync<RecycleBinMutationRequest, RecycleBinMutationResultDto>(
            "recycle-bin/purge",
            new RecycleBinMutationRequest { Items = items.ToList() },
            ct,
            expectedOwner: owner);

    public async Task<List<RecycleBinEntryDto>> GetRecycleBinAsync(
        string? kind = null,
        string? searchText = null,
        CancellationToken ct = default)
        => await GetAsync<List<RecycleBinEntryDto>>(BuildQuery("recycle-bin", ("kind", kind), ("q", searchText)), ct) ?? new List<RecycleBinEntryDto>();

    public async Task<List<RecycleBinEntryDto>> GetRecycleBinAsync(
        string? kind,
        string? searchText,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => await GetAsync<List<RecycleBinEntryDto>>(
               BuildQuery(
                   "recycle-bin",
                   ("kind", kind),
                   ("q", searchText)),
               ct,
               expectedOwner: owner) ??
           new List<RecycleBinEntryDto>();

    public Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
        => GetAsync<SyncPullResponse>($"sync/pull?sinceRev={sinceRevision}", ct);

    public Task<SyncPullResponse?> PullAsync(
        long sinceRevision,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => GetAsync<SyncPullResponse>(
            $"sync/pull?sinceRev={sinceRevision}",
            ct,
            expectedOwner: owner);

    public Task<SyncPushResult?> PushAsync(SyncPushRequest request, CancellationToken ct = default)
        => PostAsync<SyncPushRequest, SyncPushResult>("sync/push", request, ct);

    public Task<SyncPushResult?> PushAsync(
        SyncPushRequest request,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => PostAsync<SyncPushRequest, SyncPushResult>(
            "sync/push",
            request,
            ct,
            expectedOwner: owner);

    public Task<SyncStatusDto?> GetSyncStatusAsync(CancellationToken ct = default)
        => GetAsync<SyncStatusDto>("sync/status", ct);

    public Task<SyncStatusDto?> GetSyncStatusAsync(
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => GetAsync<SyncStatusDto>(
            "sync/status",
            ct,
            expectedOwner: owner);

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

    public Task<InvoiceDto?> CreateInvoiceAsync(
        InvoiceDto request,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => PostAsync<InvoiceDto, InvoiceDto>(
            "invoices",
            request,
            ct,
            expectedOwner: owner);

    public Task<InvoiceDto?> UpdateInvoiceAsync(InvoiceDto request, CancellationToken ct = default)
        => PutAsync<InvoiceDto, InvoiceDto>($"invoices/{request.Id}", request, ct);

    public Task<InvoiceDto?> UpdateInvoiceAsync(
        InvoiceDto request,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => PutAsync<InvoiceDto, InvoiceDto>(
            $"invoices/{request.Id}",
            request,
            ct,
            expectedOwner: owner);

    public Task<PaymentDto?> CreatePaymentAsync(PaymentDto request, CancellationToken ct = default)
        => PostAsync<PaymentDto, PaymentDto>("payments", request, ct);

    public Task<PaymentDto?> CreatePaymentAsync(
        PaymentDto request,
        MobileSessionOwner owner,
        CancellationToken ct = default)
        => PostAsync<PaymentDto, PaymentDto>(
            "payments",
            request,
            ct,
            expectedOwner: owner);

    public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(Guid paymentId, PendingPaymentAttachmentRecord attachment, CancellationToken ct = default)
        => await UploadPaymentAttachmentAsync(
            paymentId,
            attachment,
            _sessionStore.CaptureOwner(),
            ct);

    public async Task<PaymentAttachmentDto?> UploadPaymentAttachmentAsync(
        Guid paymentId,
        PendingPaymentAttachmentRecord attachment,
        MobileSessionOwner expectedOwner,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(
            owner => CreatePaymentAttachmentUploadRequestAsync(
                paymentId,
                attachment,
                owner ??
                throw new InvalidOperationException(
                    "The owner-bound attachment upload request is missing its expected owner."),
                ct),
            $"payments/{paymentId}/attachments",
            ct,
            expectedOwner: expectedOwner,
            requestTimeout: FileTransferRequestTimeout);
        await EnsureSuccessForOwnerAsync(
            response,
            $"payments/{paymentId}/attachments",
            expectedOwner,
            ct);
        var result = await response.Content.ReadFromJsonAsync<PaymentAttachmentDto>(
            _jsonOptions,
            ct);
        _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        return result;
    }

    private async Task<HttpRequestMessage> CreatePaymentAttachmentUploadRequestAsync(
        Guid paymentId,
        PendingPaymentAttachmentRecord attachment,
        MobileSessionOwner expectedOwner,
        CancellationToken ct)
    {
        _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        var request = await CreateRequestAsync(
            HttpMethod.Post,
            $"payments/{paymentId}/attachments",
            expectedOwner: expectedOwner,
            cancellationToken: ct);
        FileStream? fileStream = null;
        MultipartFormDataContent? form = null;
        try
        {
            fileStream = new FileStream(
                attachment.StoredPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await PaymentAttachmentUploadIntegrity.ValidateAndRewindAsync(
                fileStream,
                attachment,
                ct);
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            form = new MultipartFormDataContent();
            var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = MediaTypeHeaderValue.Parse(
                string.IsNullOrWhiteSpace(attachment.MimeType)
                    ? "application/octet-stream"
                    : attachment.MimeType);

            form.Add(fileContent, "file", attachment.FileName);
            form.Add(new StringContent(attachment.AttachmentType ?? "내역첨부"), "attachmentType");
            form.Add(new StringContent(attachment.Description ?? string.Empty), "description");
            if (attachment.LocalId != Guid.Empty)
                form.Add(new StringContent(attachment.LocalId.ToString("D")), "clientAttachmentId");
            request.Content = form;
            return request;
        }
        catch
        {
            form?.Dispose();
            fileStream?.Dispose();
            request.Dispose();
            throw;
        }
    }

    private async Task<T?> GetAsync<T>(
        string relative,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null,
        MobileSessionOwner? expectedOwner = null)
    {
        expectedOwner ??= requireAuthentication
            ? _sessionStore.CaptureOwner()
            : null;
        using var response = await SendAsync(
            owner => CreateRequestAsync(
                HttpMethod.Get,
                relative,
                requireAuthentication,
                owner,
                ct),
            relative,
            ct,
            requireAuthentication,
            requestTimeout,
            expectedOwner);
        await EnsureSuccessForOwnerAsync(
            response,
            relative,
            expectedOwner,
            ct);
        var result = await response.Content.ReadFromJsonAsync<T>(
            _jsonOptions,
            ct);
        if (expectedOwner is not null)
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        return result;
    }

    private async Task<TResponse?> PostAsync<TRequest, TResponse>(
        string relative,
        TRequest payload,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null,
        MobileSessionOwner? expectedOwner = null)
    {
        expectedOwner ??= requireAuthentication
            ? _sessionStore.CaptureOwner()
            : null;
        using var response = await SendAsync(
            async owner =>
            {
                var request = await CreateRequestAsync(
                    HttpMethod.Post,
                    relative,
                    requireAuthentication,
                    owner,
                    ct);
                request.Content = JsonContent.Create(payload, options: _jsonOptions);
                return request;
            },
            relative,
            ct,
            requireAuthentication,
            requestTimeout,
            expectedOwner);
        await EnsureSuccessForOwnerAsync(
            response,
            relative,
            expectedOwner,
            ct);
        var result =
            await response.Content.ReadFromJsonAsync<TResponse>(
                _jsonOptions,
                ct);
        if (expectedOwner is not null)
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        return result;
    }

    private async Task<TResponse?> PutAsync<TRequest, TResponse>(
        string relative,
        TRequest payload,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null,
        MobileSessionOwner? expectedOwner = null)
    {
        expectedOwner ??= requireAuthentication
            ? _sessionStore.CaptureOwner()
            : null;
        using var response = await SendAsync(
            async owner =>
            {
                var request = await CreateRequestAsync(
                    HttpMethod.Put,
                    relative,
                    requireAuthentication,
                    owner,
                    ct);
                request.Content = JsonContent.Create(payload, options: _jsonOptions);
                return request;
            },
            relative,
            ct,
            requireAuthentication,
            requestTimeout,
            expectedOwner);
        await EnsureSuccessForOwnerAsync(
            response,
            relative,
            expectedOwner,
            ct);
        var result =
            await response.Content.ReadFromJsonAsync<TResponse>(
                _jsonOptions,
                ct);
        if (expectedOwner is not null)
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        return result;
    }

    private async Task DeleteAsync(
        string relative,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null,
        MobileSessionOwner? expectedOwner = null)
    {
        expectedOwner ??= requireAuthentication
            ? _sessionStore.CaptureOwner()
            : null;
        using var response = await SendAsync(
            owner => CreateRequestAsync(
                HttpMethod.Delete,
                relative,
                requireAuthentication,
                owner,
                ct),
            relative,
            ct,
            requireAuthentication,
            requestTimeout,
            expectedOwner);
        await EnsureSuccessForOwnerAsync(
            response,
            relative,
            expectedOwner,
            ct);
        if (expectedOwner is not null)
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relative,
        bool requireAuthentication = true,
        MobileSessionOwner? expectedOwner = null,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(method, BuildUri(relative));
        if (!requireAuthentication)
            return request;

        expectedOwner ??= _sessionStore.CaptureOwner();
        _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        var token = await GetAccessTokenAsync(
            relative,
            expectedOwner,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(token))
        {
            await HandleAuthenticationFailureAsync(
                expectedOwner);
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
        var leafName = Path.GetFileName(
            fileName
                .Replace(
                    Path.AltDirectorySeparatorChar,
                    Path.DirectorySeparatorChar)
                .Trim());
        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = string.Join(
                "_",
                leafName.Split(
                    invalid,
                    StringSplitOptions.RemoveEmptyEntries))
            .Trim()
            .Trim('.');
        if (string.IsNullOrWhiteSpace(sanitized))
            return "attachment.bin";

        const int maxFileNameLength = 120;
        return sanitized.Length <= maxFileNameLength
            ? sanitized
            : sanitized[..maxFileNameLength];
    }

    private static string ResolveAuthenticatedDownloadCacheRoot(
        MobileSessionOwner owner,
        string category)
    {
        var ownerKey = owner.BuildStateKey();
        if (string.Equals(
                ownerKey,
                "legacy",
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Authenticated mobile owner is required for cached downloads.");
        }

        var ownerHash = Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(ownerKey)))
            .ToLowerInvariant();
        return Path.Combine(
            FileSystem.CacheDirectory,
            "authenticated-downloads",
            ownerHash,
            category);
    }

    private async Task<string?> GetAccessTokenAsync(
        string relative,
        MobileSessionOwner expectedOwner,
        CancellationToken ct)
    {
        _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        var token = await _sessionStore.GetTokenAsync(clearStaleSession: false);
        if (!string.IsNullOrWhiteSpace(token))
        {
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            return token;
        }

        var recovery = await _sessionRecovery.TryRestoreSessionAsync($"token:{relative}", ct);
        if (recovery.Success)
        {
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            return await _sessionStore.GetTokenAsync(
                clearStaleSession: false);
        }

        return null;
    }

    private async Task<HttpResponseMessage> SendAsync(
        Func<MobileSessionOwner?, Task<HttpRequestMessage>>
            requestFactory,
        string relative,
        CancellationToken ct,
        bool requireAuthentication = true,
        TimeSpan? requestTimeout = null,
        MobileSessionOwner? expectedOwner = null)
    {
#if DEBUG
        await MobileDiagnosticFaultInjector.ThrowIfConfiguredAsync(relative, ct);
#endif
        expectedOwner ??= requireAuthentication
            ? _sessionStore.CaptureOwner()
            : null;
        if (expectedOwner is not null)
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        var response = await SendCoreAsync(
            () => CreateOwnerBoundRequestAsync(
                requestFactory,
                expectedOwner),
            ct,
            requestTimeout ?? DefaultApiRequestTimeout);
        if (expectedOwner is not null &&
            !_sessionStore.IsOwnerCurrent(expectedOwner))
        {
            response.Dispose();
            throw new StaleMobileSessionOwnerException(
                "The mobile owner changed while the API request was in flight.");
        }
        if (!requireAuthentication || response.StatusCode != HttpStatusCode.Unauthorized)
            return response;

        response.Dispose();

        var recovery = await _sessionRecovery.TryRestoreSessionAsync($"401:{relative}", forceRefresh: true, ct: ct);
        if (recovery.Success)
        {
            if (expectedOwner is not null)
                _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            var retryResponse = await SendCoreAsync(
                () => CreateOwnerBoundRequestAsync(
                    requestFactory,
                    expectedOwner),
                ct,
                requestTimeout ?? DefaultApiRequestTimeout);
            if (expectedOwner is not null &&
                !_sessionStore.IsOwnerCurrent(expectedOwner))
            {
                retryResponse.Dispose();
                throw new StaleMobileSessionOwnerException(
                    "The mobile owner changed while the recovered API request was in flight.");
            }
            if (retryResponse.StatusCode != HttpStatusCode.Unauthorized)
                return retryResponse;

            retryResponse.Dispose();
        }

        if (expectedOwner is not null)
        {
            await HandleAuthenticationFailureAsync(
                expectedOwner);
        }
        throw new MobileAuthenticationException(relative,
            $"401 Unauthorized ({relative}): 저장된 Bearer 토큰이 만료되었거나 권한/담당지점/사업 범위가 변경되어 자동 로그인으로도 복구하지 못했습니다. 다시 로그인해 주세요.".Trim());
    }

    private async Task<HttpRequestMessage>
        CreateOwnerBoundRequestAsync(
            Func<MobileSessionOwner?, Task<HttpRequestMessage>>
                requestFactory,
            MobileSessionOwner? expectedOwner)
    {
        var request = await requestFactory(expectedOwner);
        if (expectedOwner is not null)
        {
            try
            {
                _sessionStore.ThrowIfOwnerChanged(
                    expectedOwner);
            }
            catch
            {
                request.Dispose();
                throw;
            }
        }

        return request;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        Func<Task<HttpRequestMessage>> requestFactory,
        CancellationToken ct,
        TimeSpan requestTimeout)
    {
        using var request = await requestFactory();
        _clientIdentity.Apply(request);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(requestTimeout);
        return await _http.SendAsync(request, timeoutCts.Token);
    }

    private async Task EnsureSuccessForOwnerAsync(
        HttpResponseMessage response,
        string relative,
        MobileSessionOwner? expectedOwner,
        CancellationToken ct)
    {
        try
        {
            await EnsureSuccessAsync(
                response,
                relative,
                ct,
                expectedOwner);
        }
        catch (MobileAuthenticationException)
        {
            if (expectedOwner is not null &&
                _sessionStore.GetSnapshot().IsAuthenticated)
            {
                _sessionStore.ThrowIfOwnerChanged(
                    expectedOwner);
            }

            throw;
        }
        catch
        {
            if (expectedOwner is not null)
            {
                _sessionStore.ThrowIfOwnerChanged(
                    expectedOwner);
            }

            throw;
        }

        if (expectedOwner is not null)
        {
            _sessionStore.ThrowIfOwnerChanged(
                expectedOwner);
        }
    }

    private async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string relative,
        CancellationToken ct,
        MobileSessionOwner? expectedOwner = null)
    {
        if (response.IsSuccessStatusCode)
            return;

        if (response.StatusCode == HttpStatusCode.UpgradeRequired)
        {
            var upgradeException =
                await MobileUpgradeRequiredResponseParser
                    .CreateExceptionAndPublishAsync(
                        relative,
                        response.Content,
                        CancellationToken.None)
                    .ConfigureAwait(false);
            throw upgradeException;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var failureMessage = ApiErrorMessageFormatter.BuildFailureMessage(
            response.StatusCode,
            response.ReasonPhrase,
            body);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            await HandleAuthenticationFailureAsync(
                expectedOwner);
            throw new MobileAuthenticationException(relative,
                $"401 Unauthorized ({relative}): 서버가 Bearer 토큰을 거부했습니다. 세션이 만료되었거나 권한/담당지점/사업 범위가 변경되었을 수 있습니다. 다시 로그인해 주세요. {failureMessage}".Trim());
        }

        throw new HttpRequestException(failureMessage, null, response.StatusCode);
    }

    private async Task HandleAuthenticationFailureAsync(
        MobileSessionOwner? expectedOwner = null)
    {
        var cleared = expectedOwner is null
            ? await ClearUnconditionallyAsync()
            : await _sessionStore.ClearIfCurrentAsync(
                expectedOwner);
        if (cleared)
            MainThread.BeginInvokeOnMainThread(App.ShowLogin);
    }

    private async Task<bool> ClearUnconditionallyAsync()
    {
        await _sessionStore.ClearAsync();
        return true;
    }

    private async Task DownloadFileToCacheAsync(
        string relative,
        string cachedPath,
        long expectedSize,
        string? expectedSha256,
        string label,
        MobileSessionOwner expectedOwner,
        CancellationToken ct)
    {
        _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        var temporaryPath =
            $"{cachedPath}.download.{Guid.NewGuid():N}";

        try
        {
            using var response = await SendAsync(
                owner => CreateRequestAsync(
                    HttpMethod.Get,
                    relative,
                    expectedOwner: owner,
                    cancellationToken: ct),
                relative,
                ct,
                requestTimeout: FileTransferRequestTimeout,
                expectedOwner: expectedOwner);
            await EnsureSuccessForOwnerAsync(
                response,
                relative,
                expectedOwner,
                ct);
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(temporaryPath))
            {
                await source.CopyToAsync(target, ct);
                await target.FlushAsync(ct);
            }

            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            await ValidateDownloadedFileAsync(temporaryPath, expectedSize, expectedSha256, label, ct);
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            Directory.CreateDirectory(Path.GetDirectoryName(cachedPath)!);
            using (await _sessionStore.AcquireOwnerCommitLeaseAsync(
                       expectedOwner,
                       ct))
            {
                File.Move(
                    temporaryPath,
                    cachedPath,
                    overwrite: true);
                _sessionStore.ThrowIfOwnerChanged(
                    expectedOwner);
            }
        }
        catch
        {
            TryDeleteFile(temporaryPath);
            throw;
        }
    }

    private async Task DeleteCachedDownloadIfCurrentOwnerAsync(
        string cachedPath,
        MobileSessionOwner expectedOwner,
        CancellationToken ct)
    {
        using (await _sessionStore.AcquireOwnerCommitLeaseAsync(
                   expectedOwner,
                   ct))
        {
            TryDeleteFile(cachedPath);
            _sessionStore.ThrowIfOwnerChanged(
                expectedOwner);
        }
    }

    private async Task CleanupCachedDownloadTemporaryFilesAsync(
        string cachedPath,
        MobileSessionOwner expectedOwner,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(cachedPath);
        if (string.IsNullOrWhiteSpace(directory) ||
            !Directory.Exists(directory))
        {
            return;
        }

        var staleBeforeUtc =
            DateTime.UtcNow.Subtract(
                FileTransferRequestTimeout);
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        using (await _sessionStore.AcquireOwnerCommitLeaseAsync(
                   expectedOwner,
                   ct))
        {
            foreach (var temporaryPath in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.TopDirectoryOnly))
            {
                ct.ThrowIfCancellationRequested();
                if (!Path.GetFileName(temporaryPath).Contains(
                        ".download.",
                        pathComparison))
                {
                    continue;
                }

                if (File.GetLastWriteTimeUtc(temporaryPath) >=
                    staleBeforeUtc)
                {
                    continue;
                }

                TryDeleteFile(temporaryPath);
            }

            _sessionStore.ThrowIfOwnerChanged(
                expectedOwner);
        }
    }

    private async Task WriteBytesToCacheAsync(
        string cachedPath,
        byte[] bytes,
        long expectedSize,
        string? expectedSha256,
        string label,
        MobileSessionOwner expectedOwner,
        CancellationToken ct)
    {
        _sessionStore.ThrowIfOwnerChanged(expectedOwner);
        var temporaryPath =
            $"{cachedPath}.download.{Guid.NewGuid():N}";

        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, ct);
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            await ValidateDownloadedFileAsync(temporaryPath, expectedSize, expectedSha256, label, ct);
            _sessionStore.ThrowIfOwnerChanged(expectedOwner);
            Directory.CreateDirectory(
                Path.GetDirectoryName(cachedPath)!);
            using (await _sessionStore.AcquireOwnerCommitLeaseAsync(
                       expectedOwner,
                       ct))
            {
                File.Move(
                    temporaryPath,
                    cachedPath,
                    overwrite: true);
                _sessionStore.ThrowIfOwnerChanged(
                    expectedOwner);
            }
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
