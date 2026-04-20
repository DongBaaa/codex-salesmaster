using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Thin wrapper around the 거래플랜 server REST API.
/// </summary>
public sealed class ErpApiClient
{
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();
    private static readonly JsonSerializerOptions ConflictPayloadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly SessionState _session;

    public ErpApiClient(HttpClient http, SessionState session)
    {
        _http = http;
        _session = session;

        if (_http.Timeout != Timeout.InfiniteTimeSpan)
            _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    private void SetAuthHeader(bool includeBusinessDatabaseHeader = false, string? businessDatabaseNameOverride = null)
    {
        _http.DefaultRequestHeaders.Authorization = null;
        if (_session.Token is not null)
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _session.Token);

        const string tenantHeaderName = "X-Tenant-Code";
        if (_http.DefaultRequestHeaders.Contains(tenantHeaderName))
            _http.DefaultRequestHeaders.Remove(tenantHeaderName);

        if (!includeBusinessDatabaseHeader || !_session.HasAdministrativePrivileges)
            return;

        var headerValue = ResolveBusinessDatabaseHeaderValue(businessDatabaseNameOverride);
        if (string.IsNullOrWhiteSpace(headerValue))
            return;

        _http.DefaultRequestHeaders.TryAddWithoutValidation(tenantHeaderName, headerValue);
    }

    private string ResolveBusinessDatabaseHeaderValue(string? businessDatabaseNameOverride)
    {
        var requestedDatabaseName = string.IsNullOrWhiteSpace(businessDatabaseNameOverride)
            ? _session.SelectedBusinessDatabaseName
            : businessDatabaseNameOverride;

        return string.IsNullOrWhiteSpace(requestedDatabaseName)
            ? string.Empty
            : TenantScopeCatalog.GetDatabaseName(requestedDatabaseName);
    }

    // ── Auth ──────────────────────────────────────────────────────────────────
    public async Task<LoginResponse?> LoginAsync(string username, string password, CancellationToken ct = default)
    {
        const string operationName = "로그인(auth/login)";
        Exception? lastException = null;
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var timeoutCts = CreateOperationTimeoutTokenSource(operationName, ct);
                using var response = await _http.PostAsJsonAsync(
                    "auth/login",
                    new LoginRequest { Username = username, Password = password },
                    timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<LoginResponse>(timeoutCts.Token);

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return null;

                var message = await BuildFailureMessageAsync(response, timeoutCts.Token);
                var retryable = ShouldRetry(response.StatusCode) && attempt < MaxRetryCount;
                if (!retryable)
                    throw await CreateFailureExceptionAsync(operationName, response, timeoutCts.Token);

                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                lastException = ex;
                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        if (lastException is ExpectedRevisionConflictException)
            ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new HttpRequestException(
            $"{operationName} 실패 (최대 재시도 {MaxRetryCount}회): {lastException?.Message}",
            lastException);
    }

    public async Task<List<UserAccountDto>> GetUsersAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
                   operationName: "사용자 목록(users)",
                   sendAsync: async token =>
                   {
                       SetAuthHeader(includeBusinessDatabaseHeader: false);
                       return await _http.GetAsync("users", token);
                   },
                   readAsync: static async (resp, token) =>
                       await resp.Content.ReadFromJsonAsync<List<UserAccountDto>>(token) ?? new List<UserAccountDto>(),
                   ct)
               ?? new List<UserAccountDto>();
    }

    public async Task<UserAccountDto?> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "사용자 생성(users)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PostAsJsonAsync("users", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<UserAccountDto>(token),
            ct);
    }

    public async Task<UserAccountDto?> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "사용자 수정(users)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"users/{userId}", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<UserAccountDto>(token),
            ct);
    }

    public async Task UpdateUserPasswordAsync(Guid userId, UpdateUserPasswordRequest request, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(
            operationName: "사용자 비밀번호 수정(users/password)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"users/{userId}/password", request, token);
            },
            readAsync: static (_, _) => Task.FromResult<object?>(new object()),
            ct);
    }

    public async Task DeleteUserAsync(Guid userId, long? expectedRevision = null, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(
            operationName: "사용자 삭제(users)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.DeleteAsync(WithExpectedRevision($"users/{userId}", expectedRevision), token);
            },
            readAsync: static (_, _) => Task.FromResult<object?>(new object()),
            ct);
    }

    public async Task<TenantConfigurationSnapshotDto?> GetTenantConfigurationAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "업체/데이터 권한 조회(tenant-settings)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.GetAsync("tenant-settings", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<TenantConfigurationSnapshotDto>(token),
            ct);
    }

    public async Task<TenantDefinitionDto?> UpdateTenantDefinitionAsync(string tenantCode, UpdateTenantDefinitionRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "업체권역 저장(tenant-settings/tenants)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"tenant-settings/tenants/{Uri.EscapeDataString(tenantCode)}", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<TenantDefinitionDto>(token),
            ct);
    }

    public async Task<TenantOfficeDefinitionDto?> UpdateTenantOfficeDefinitionAsync(string officeCode, UpdateTenantOfficeDefinitionRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "지점 정의 저장(tenant-settings/offices)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"tenant-settings/offices/{Uri.EscapeDataString(officeCode)}", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<TenantOfficeDefinitionDto>(token),
            ct);
    }

    public async Task<DataSharingPolicyDto?> CreateSharingPolicyAsync(UpsertDataSharingPolicyRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "연동 정책 생성(tenant-settings/sharing-policies)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PostAsJsonAsync("tenant-settings/sharing-policies", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<DataSharingPolicyDto>(token),
            ct);
    }

    public async Task<DataSharingPolicyDto?> UpdateSharingPolicyAsync(Guid policyId, UpsertDataSharingPolicyRequest request, CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "연동 정책 저장(tenant-settings/sharing-policies)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"tenant-settings/sharing-policies/{policyId}", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<DataSharingPolicyDto>(token),
            ct);
    }

    public async Task DeleteSharingPolicyAsync(Guid policyId, long? expectedRevision = null, CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(
            operationName: "연동 정책 삭제(tenant-settings/sharing-policies)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.DeleteAsync(WithExpectedRevision($"tenant-settings/sharing-policies/{policyId}", expectedRevision), token);
            },
            readAsync: static (_, _) => Task.FromResult<object?>(new object()),
            ct);
    }

    // ── Sync ──────────────────────────────────────────────────────────────────
    public async Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
        => await PullAsync(sinceRevision, businessDatabaseNameOverride: null, ct);

    public async Task<SyncPullResponse?> PullAsync(
        long sinceRevision,
        string? businessDatabaseNameOverride,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "동기화 다운로드(sync/pull)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
                return await _http.GetAsync($"sync/pull?sinceRev={sinceRevision}", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<SyncPullResponse>(token),
            ct);
    }

    public async Task<SyncPushResult?> PushAsync(SyncPushRequest request, CancellationToken ct = default)
        => await PushAsync(request, businessDatabaseNameOverride: null, ct);

    public async Task<SyncPushResult?> PushAsync(
        SyncPushRequest request,
        string? businessDatabaseNameOverride,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "동기화 업로드(sync/push)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
                return await _http.PostAsJsonAsync("sync/push", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<SyncPushResult>(token),
            ct);
    }

    public async Task<SyncStatusDto?> GetSyncStatusAsync(CancellationToken ct = default)
        => await GetSyncStatusAsync(businessDatabaseNameOverride: null, ct);

    public async Task<SyncStatusDto?> GetSyncStatusAsync(
        string? businessDatabaseNameOverride,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "동기화 상태 조회(sync/status)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
                return await _http.GetAsync("sync/status", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<SyncStatusDto>(token),
            ct);
    }

    public async Task<EditSessionHeartbeatResponse?> HeartbeatEditSessionAsync(
        EditSessionHeartbeatRequest request,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "편집 세션 하트비트(runtime/edit-sessions)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.PostAsJsonAsync("runtime/edit-sessions/heartbeat", request, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<EditSessionHeartbeatResponse>(token),
            ct);
    }

    public async Task ReleaseEditSessionAsync(
        EditSessionReleaseRequest request,
        CancellationToken ct = default)
    {
        await ExecuteWithRetryAsync(
            operationName: "편집 세션 종료(runtime/edit-sessions)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.PostAsJsonAsync("runtime/edit-sessions/release", request, token);
            },
            readAsync: static (_, _) => Task.FromResult<object?>(new object()),
            ct);
    }

    public async Task<EditSessionLookupResponse?> GetActiveEditSessionsAsync(
        string entityType,
        string entityId,
        Guid? excludeAppSessionId = null,
        CancellationToken ct = default)
    {
        var query = BuildQuery(
            "runtime/edit-sessions/active",
            ("entityType", entityType),
            ("entityId", entityId),
            ("excludeAppSessionId", excludeAppSessionId?.ToString("D")));

        return await ExecuteWithRetryAsync(
            operationName: "활성 편집 세션 조회(runtime/edit-sessions/active)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.GetAsync(query, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<EditSessionLookupResponse>(token),
            ct);
    }

    public async Task<ScopeMatrixSnapshotDto?> GetScopeMatrixAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "권한 범위 매트릭스 조회(runtime/scope-matrix)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.GetAsync("runtime/scope-matrix", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<ScopeMatrixSnapshotDto>(token),
            ct);
    }

    public async Task<IntegrityReportDto?> GetIntegrityReportAsync(CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "서버 무결성 리포트 조회(integrity/report)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.GetAsync("integrity/report", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<IntegrityReportDto>(token),
            ct);
    }

    public async Task<IntegrityIssueDetailResultDto?> GetIntegrityIssueDetailsAsync(string code, CancellationToken ct = default)
    {
        var query = BuildQuery("integrity/report/details", ("code", code));
        return await ExecuteWithRetryAsync(
            operationName: "서버 무결성 상세 목록 조회(integrity/report/details)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.GetAsync(query, token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<IntegrityIssueDetailResultDto>(token),
            ct);
    }

    public async Task<AppUpdateManifestDto?> GetUpdateManifestAsync(string channel = "stable", CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "업데이트 매니페스트 조회(updates/manifest)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.GetAsync($"updates/manifest?channel={Uri.EscapeDataString(channel)}", token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<AppUpdateManifestDto>(token),
            ct);
    }

    public string ResolveAbsoluteUrl(string relativeOrAbsolute)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsolute))
            return string.Empty;

        if (Uri.TryCreate(relativeOrAbsolute, UriKind.Absolute, out var absoluteUri))
            return absoluteUri.ToString();

        var baseAddress = GetBaseUri();
        return new Uri(baseAddress, relativeOrAbsolute.TrimStart('/')).ToString();
    }

    public Uri GetBaseUri()
        => _http.BaseAddress ?? throw new InvalidOperationException("API 기본 주소가 설정되지 않았습니다.");

    public IReadOnlyDictionary<string, string> GetUpdateDownloadHeaders(Uri packageUri)
    {
        ArgumentNullException.ThrowIfNull(packageUri);

        if (string.IsNullOrWhiteSpace(_session.Token))
            return EmptyHeaders;

        var baseUri = GetBaseUri();
        if (!UrisShareAuthority(baseUri, packageUri))
            return EmptyHeaders;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = $"Bearer {_session.Token.Trim()}"
        };
    }

    public async Task<List<RecycleBinEntryDto>> GetRecycleBinAsync(
        string? kind = null,
        string? searchText = null,
        CancellationToken ct = default)
    {
        var query = BuildQuery("recycle-bin", ("kind", kind), ("q", searchText));
        return await ExecuteWithRetryAsync(
                   operationName: "휴지통 조회(recycle-bin)",
                   sendAsync: async token =>
                   {
                       SetAuthHeader(includeBusinessDatabaseHeader: true);
                       return await _http.GetAsync(query, token);
                   },
                   readAsync: static async (resp, token) =>
                       await resp.Content.ReadFromJsonAsync<List<RecycleBinEntryDto>>(token) ?? new List<RecycleBinEntryDto>(),
                   ct)
               ?? new List<RecycleBinEntryDto>();
    }

    public async Task<RecycleBinMutationResultDto?> RestoreRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "휴지통 복원(recycle-bin/restore)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.PostAsJsonAsync(
                    "recycle-bin/restore",
                    new RecycleBinMutationRequest { Items = items.ToList() },
                    token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<RecycleBinMutationResultDto>(token),
            ct);
    }

    public async Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "휴지통 영구삭제(recycle-bin/purge)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.PostAsJsonAsync(
                    "recycle-bin/purge",
                    new RecycleBinMutationRequest { Items = items.ToList() },
                    token);
            },
            readAsync: static (resp, token) => resp.Content.ReadFromJsonAsync<RecycleBinMutationResultDto>(token),
            ct);
    }

    private static bool ShouldRetry(HttpStatusCode code)
    {
        return code == HttpStatusCode.RequestTimeout
            || (int)code == 429
            || code == HttpStatusCode.InternalServerError
            || code == HttpStatusCode.BadGateway
            || code == HttpStatusCode.ServiceUnavailable
            || code == HttpStatusCode.GatewayTimeout;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct)
    {
        if (ex is TaskCanceledException && !ct.IsCancellationRequested)
            return true;

        if (ex is TimeoutException)
            return true;

        if (ex is HttpRequestException httpEx)
            return httpEx.StatusCode is null || ShouldRetry(httpEx.StatusCode.Value);

        return false;
    }

    private static async Task<string> BuildFailureMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);
        if (body.Length > 200)
            body = body[..200] + "...";
        return $"{(int)response.StatusCode} {response.ReasonPhrase} {body}".Trim();
    }

    private static bool UrisShareAuthority(Uri left, Uri right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        return string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase)
               && left.Port == right.Port;
    }

    private static string BuildQuery(string path, params (string Key, string? Value)[] query)
    {
        var items = query
            .Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}")
            .ToList();

        return items.Count == 0 ? path : $"{path}?{string.Join("&", items)}";
    }

    private async Task<T?> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<HttpResponseMessage, CancellationToken, Task<T?>> readAsync,
        CancellationToken ct)
    {
        Exception? lastException = null;
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var timeoutCts = CreateOperationTimeoutTokenSource(operationName, ct);
                using var response = await sendAsync(timeoutCts.Token);
                if (response.IsSuccessStatusCode)
                    return await readAsync(response, timeoutCts.Token);

                var message = await BuildFailureMessageAsync(response, timeoutCts.Token);
                var retryable = ShouldRetry(response.StatusCode) && attempt < MaxRetryCount;
                if (!retryable)
                    throw await CreateFailureExceptionAsync(operationName, response, timeoutCts.Token);

                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                lastException = ex;
                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        if (lastException is ExpectedRevisionConflictException)
            ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new HttpRequestException(
            $"{operationName} 실패 (최대 재시도 {MaxRetryCount}회): {lastException?.Message}",
            lastException);
    }

    private static string WithExpectedRevision(string relativePath, long? expectedRevision)
    {
        if (expectedRevision is not > 0)
            return relativePath;

        var separator = relativePath.Contains('?') ? '&' : '?';
        return $"{relativePath}{separator}expectedRevision={expectedRevision.Value}";
    }

    private static CancellationTokenSource CreateOperationTimeoutTokenSource(string operationName, CancellationToken ct)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(GetOperationTimeout(operationName));
        return timeoutCts;
    }

    private static TimeSpan GetOperationTimeout(string operationName)
    {
        if (operationName.Contains("동기화 업로드(sync/push)", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(10);

        if (operationName.Contains("휴지통 영구삭제", StringComparison.OrdinalIgnoreCase) ||
            operationName.Contains("휴지통 복원", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(5);

        if (operationName.Contains("동기화 다운로드(sync/pull)", StringComparison.OrdinalIgnoreCase) ||
            operationName.Contains("서버 무결성 리포트 조회", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(2);

        return TimeSpan.FromSeconds(30);
    }

    private async Task<Exception> CreateFailureExceptionAsync(string operationName, HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var payload = TryParseExpectedRevisionConflict(body);
            if (payload is not null)
            {
                var conflictReason = await BuildConflictReasonAsync(payload, ct);
                return new ExpectedRevisionConflictException(
                    payload.EntityName,
                    payload.EntityId,
                    payload.ExpectedRevision,
                    payload.CurrentRevision,
                    conflictReason);
            }
        }

        var message = BuildFailureMessage(response, body);
        return new HttpRequestException($"{operationName} 실패: {message}", null, response.StatusCode);
    }

    private async Task<string> BuildConflictReasonAsync(ExpectedRevisionConflictPayload payload, CancellationToken ct)
    {
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(payload.Reason))
            reasons.Add(payload.Reason.Trim());

        var activeEditors = await TryGetActiveEditorsForConflictAsync(payload, ct);
        if (activeEditors.Count > 0)
        {
            reasons.Add("현재 서버 기준 활성 편집 세션");
            reasons.AddRange(activeEditors.Select(editor =>
                $"- {editor.Username} / {editor.OfficeCode} / {editor.MachineName}"));
        }

        return string.Join(Environment.NewLine, reasons.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private async Task<List<EditSessionParticipantDto>> TryGetActiveEditorsForConflictAsync(ExpectedRevisionConflictPayload payload, CancellationToken ct)
    {
        try
        {
            if (payload.EntityId == Guid.Empty || string.IsNullOrWhiteSpace(payload.EntityName) || !_session.IsLoggedIn || _session.IsOfflineMode)
                return new List<EditSessionParticipantDto>();

            SetAuthHeader(includeBusinessDatabaseHeader: true);
            var query = BuildQuery(
                "runtime/edit-sessions/active",
                ("entityType", payload.EntityName),
                ("entityId", payload.EntityId.ToString("D")),
                ("excludeAppSessionId", _session.SessionId.ToString("D")));

            using var timeoutCts = CreateOperationTimeoutTokenSource("활성 편집 세션 조회(runtime/edit-sessions/active)", ct);
            using var response = await _http.GetAsync(query, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
                return new List<EditSessionParticipantDto>();

            var lookup = await response.Content.ReadFromJsonAsync<EditSessionLookupResponse>(timeoutCts.Token);
            return lookup?.ActiveEditors ?? new List<EditSessionParticipantDto>();
        }
        catch
        {
            return new List<EditSessionParticipantDto>();
        }
    }

    private static ExpectedRevisionConflictPayload? TryParseExpectedRevisionConflict(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ExpectedRevisionConflictPayload>(body, ConflictPayloadJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string BuildFailureMessage(HttpResponseMessage response, string body)
    {
        var trimmedBody = body;
        if (trimmedBody.Length > 200)
            trimmedBody = trimmedBody[..200] + "...";

        return $"{(int)response.StatusCode} {response.ReasonPhrase} {trimmedBody}".Trim();
    }
}
