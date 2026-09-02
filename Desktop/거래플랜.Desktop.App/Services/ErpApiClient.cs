using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed class AmbiguousMutationOutcomeException : HttpRequestException
{
    public AmbiguousMutationOutcomeException(
        string operationName,
        Exception innerException,
        HttpStatusCode? responseStatusCode = null)
        : base(
            $"{operationName}: 요청을 한 번 전송했지만 서버 반영 결과를 확정할 수 없습니다.",
            innerException,
            responseStatusCode)
    {
        OperationName = operationName;
        ResponseStatusCode = responseStatusCode;
    }

    public string OperationName { get; }
    public HttpStatusCode? ResponseStatusCode { get; }
}

/// <summary>
/// Thin wrapper around the 거래플랜 server REST API.
/// </summary>
public sealed class ErpApiClient
{
    private const int MaxRetryCount = 3;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan TokenRefreshLeadTime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan TokenRefreshFailureCooldown = TimeSpan.FromMinutes(1);
    private static readonly IReadOnlyDictionary<string, string> EmptyHeaders = new Dictionary<string, string>();
    private static readonly JsonSerializerOptions ConflictPayloadJsonOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly HttpClient _http;
    private readonly SessionState _session;
    private readonly LocalStateService? _localState;
    private readonly IDesktopUpgradeRequiredObserver? _upgradeObserver;
    private readonly SemaphoreSlim _sessionRefreshLock = new(1, 1);
    private DateTime _lastSessionRefreshFailureAtUtc = DateTime.MinValue;

    public ErpApiClient(
        HttpClient http,
        SessionState session,
        LocalStateService? localState = null,
        DesktopClientIdentityProvider? clientIdentityProvider = null,
        IDesktopUpgradeRequiredObserver? upgradeObserver = null)
    {
        _http = http;
        _session = session;
        _localState = localState;
        _upgradeObserver = upgradeObserver;
        (clientIdentityProvider ?? new DesktopClientIdentityProvider()).Apply(_http);

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

        if (!includeBusinessDatabaseHeader || !_session.HasSystemConfigurationScope)
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
        => (await LoginWithOutcomeAsync(username, password, ct)).Response;

    internal async Task<LoginAttemptOutcome> LoginWithOutcomeAsync(
        string username,
        string password,
        CancellationToken ct = default)
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
                    return new LoginAttemptOutcome(
                        await response.Content.ReadFromJsonAsync<LoginResponse>(timeoutCts.Token),
                        response.StatusCode);

                if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return new LoginAttemptOutcome(null, response.StatusCode);

                if (response.StatusCode == HttpStatusCode.UpgradeRequired)
                {
                    throw await CreateFailureExceptionAsync(
                        operationName,
                        response,
                        timeoutCts.Token);
                }

                var message = await BuildFailureMessageAsync(response, timeoutCts.Token);
                var retryable = ShouldRetry(response.StatusCode) && attempt < MaxRetryCount;
                if (!retryable)
                    throw await CreateFailureExceptionAsync(operationName, response, timeoutCts.Token);

                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested)
            {
                throw;
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

        if (lastException is
            ExpectedRevisionConflictException or
            DesktopClientUpgradeRequiredException)
            ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new HttpRequestException(
            $"{operationName} 실패 (최대 재시도 {MaxRetryCount}회): {lastException?.Message}",
            lastException,
            ResolveHttpStatusCode(lastException));
    }

    internal sealed record LoginAttemptOutcome(
        LoginResponse? Response,
        HttpStatusCode? StatusCode);

    public async Task<LoginResponse?> RefreshSessionAsync(CancellationToken ct = default)
    {
        const string operationName = "로그인 세션 갱신(auth/refresh)";
        if (!_session.IsLoggedIn || _session.IsOfflineMode || string.IsNullOrWhiteSpace(_session.Token))
            return null;

        Exception? lastException = null;
        var delay = InitialRetryDelay;

        for (var attempt = 1; attempt <= MaxRetryCount; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                SetAuthHeader();
                using var timeoutCts = CreateOperationTimeoutTokenSource(operationName, ct);
                using var response = await _http.PostAsync("auth/refresh", content: null, timeoutCts.Token);

                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadFromJsonAsync<LoginResponse>(timeoutCts.Token);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return null;

                if (response.StatusCode == HttpStatusCode.UpgradeRequired)
                {
                    throw await CreateFailureExceptionAsync(
                        operationName,
                        response,
                        timeoutCts.Token);
                }

                var message = await BuildFailureMessageAsync(response, timeoutCts.Token);
                var retryable = ShouldRetry(response.StatusCode) && attempt < MaxRetryCount;
                if (!retryable)
                    throw await CreateFailureExceptionAsync(operationName, response, timeoutCts.Token);

                AppLogger.Warn("AUTH", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                lastException = ex;
                AppLogger.Warn("AUTH", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex)
            {
                lastException = ex;
                break;
            }
        }

        if (lastException is DesktopClientUpgradeRequiredException)
            ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new HttpRequestException(
            $"{operationName} 실패 (최대 재시도 {MaxRetryCount}회): {lastException?.Message}",
            lastException,
            ResolveHttpStatusCode(lastException));
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
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "사용자 생성(users)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PostAsJsonAsync("users", request, token);
            },
            readAsync: async (resp, token) => ValidateCreatedUserResponse(
                await resp.Content.ReadFromJsonAsync<UserAccountDto>(token),
                request),
            ct);
    }

    public async Task<UserAccountDto?> UpdateUserAsync(Guid userId, UpdateUserRequest request, CancellationToken ct = default)
    {
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "사용자 수정(users)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"users/{userId}", request, token);
            },
            readAsync: async (resp, token) => ValidateUpdatedUserResponse(
                await resp.Content.ReadFromJsonAsync<UserAccountDto>(token),
                userId,
                request),
            ct);
    }

    public async Task UpdateUserPasswordAsync(Guid userId, UpdateUserPasswordRequest request, CancellationToken ct = default)
    {
        await ExecuteNonIdempotentSingleDispatchAsync(
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
        await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "사용자 삭제(users)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.DeleteAsync(WithExpectedRevision($"users/{userId}", expectedRevision), token);
            },
            readAsync: static (_, _) => Task.FromResult<object?>(new object()),
            ct);
    }

    private static UserAccountDto ValidateCreatedUserResponse(
        UserAccountDto? response,
        CreateUserRequest request)
    {
        if (response is null ||
            response.Id == Guid.Empty ||
            response.Revision <= 0 ||
            !UserResponseFieldsMatch(
                response,
                request.Username,
                request.Role,
                request.TenantCode,
                request.OfficeCode,
                request.ScopeType,
                request.IsActive,
                request.Permissions))
        {
            throw new InvalidDataException(
                "사용자 생성 응답의 ID, revision 또는 사용자 필드가 요청과 일치하지 않습니다.");
        }

        return response;
    }

    private static UserAccountDto ValidateUpdatedUserResponse(
        UserAccountDto? response,
        Guid expectedUserId,
        UpdateUserRequest request)
    {
        if (response is null ||
            response.Id != expectedUserId ||
            response.Revision <= request.ExpectedRevision ||
            !UserResponseFieldsMatch(
                response,
                request.Username,
                request.Role,
                request.TenantCode,
                request.OfficeCode,
                request.ScopeType,
                request.IsActive,
                request.Permissions))
        {
            throw new InvalidDataException(
                "사용자 수정 응답의 ID, revision 또는 사용자 필드가 요청과 일치하지 않습니다.");
        }

        return response;
    }

    private static bool UserResponseFieldsMatch(
        UserAccountDto response,
        string username,
        string role,
        string tenantCode,
        string officeCode,
        string scopeType,
        bool isActive,
        IEnumerable<string> permissions)
    {
        if (!TenantScopeCatalog.TryNormalizeTenantCode(response.TenantCode, out var actualTenantCode) ||
            !OfficeCodeCatalog.TryNormalizeOfficeCode(response.OfficeCode, out var actualOfficeCode) ||
            !OfficeCodeCatalog.TryNormalizeOfficeCode(officeCode, out var expectedOfficeCode) ||
            !TenantScopeCatalog.TryNormalizeScopeType(response.ScopeType, out var actualScopeType) ||
            !TenantScopeCatalog.TryNormalizeScopeType(scopeType, out var expectedScopeType))
        {
            return false;
        }

        var expectedTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            tenantCode,
            officeCode);
        var expectedRole = string.Equals(role, "Admin", StringComparison.OrdinalIgnoreCase)
            ? "Admin"
            : "User";
        var expectedPermissions = permissions
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actualPermissions = response.Permissions?
            .Where(permission => !string.IsNullOrWhiteSpace(permission))
            .Select(permission => permission.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return string.Equals(response.Username, username.Trim(), StringComparison.Ordinal) &&
               string.Equals(response.Role, expectedRole, StringComparison.Ordinal) &&
               response.IsActive == isActive &&
               string.Equals(actualTenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(actualOfficeCode, expectedOfficeCode, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(actualScopeType, expectedScopeType, StringComparison.OrdinalIgnoreCase) &&
               actualPermissions is not null &&
               actualPermissions.SetEquals(expectedPermissions);
    }

    public async Task<TenantConfigurationSnapshotDto?> GetTenantConfigurationAsync(
        bool includeInactive = false,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "업체/데이터 권한 조회(tenant-settings)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                var path = includeInactive
                    ? "tenant-settings?includeInactive=true"
                    : "tenant-settings";
                return await _http.GetAsync(path, token);
            },
            readAsync: static (resp, token) => ReadRequiredJsonAsync<TenantConfigurationSnapshotDto>(
                resp,
                token,
                "업체/데이터 권한 조회(tenant-settings)"),
            ct);
    }

    public async Task<TenantDefinitionDto?> UpdateTenantDefinitionAsync(string tenantCode, UpdateTenantDefinitionRequest request, CancellationToken ct = default)
    {
        var canonicalTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(tenantCode);
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "업체권역 저장(tenant-settings/tenants)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"tenant-settings/tenants/{Uri.EscapeDataString(canonicalTenantCode)}", request, token);
            },
            readAsync: async (resp, token) => ValidateTenantMutationResponse(
                (await ReadRequiredJsonAsync<TenantDefinitionDto>(
                    resp,
                    token,
                    "업체권역 저장(tenant-settings/tenants)"))!,
                canonicalTenantCode,
                request),
            ct);
    }

    public async Task<TenantProvisioningResultDto?> ProvisionIndependentTenantAsync(
        ProvisionIndependentTenantRequest request,
        CancellationToken ct = default)
    {
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "독립 업체 DB 생성(tenant-settings/provision-independent)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PostAsJsonAsync(
                    "tenant-settings/provision-independent",
                    request,
                    token);
            },
            readAsync: static (resp, token) => ReadRequiredJsonAsync<TenantProvisioningResultDto>(
                resp,
                token,
                "독립 업체 DB 생성(tenant-settings/provision-independent)"),
            ct);
    }

    public async Task<TenantOfficeDefinitionDto?> UpdateTenantOfficeDefinitionAsync(string officeCode, UpdateTenantOfficeDefinitionRequest request, CancellationToken ct = default)
    {
        var canonicalOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(officeCode);
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "지점 정의 저장(tenant-settings/offices)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"tenant-settings/offices/{Uri.EscapeDataString(canonicalOfficeCode)}", request, token);
            },
            readAsync: async (resp, token) => ValidateOfficeMutationResponse(
                (await ReadRequiredJsonAsync<TenantOfficeDefinitionDto>(
                    resp,
                    token,
                    "지점 정의 저장(tenant-settings/offices)"))!,
                canonicalOfficeCode,
                request),
            ct);
    }

    public async Task<DataSharingPolicyDto?> CreateSharingPolicyAsync(UpsertDataSharingPolicyRequest request, CancellationToken ct = default)
    {
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "연동 정책 생성(tenant-settings/sharing-policies)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PostAsJsonAsync("tenant-settings/sharing-policies", request, token);
            },
            readAsync: async (resp, token) => ValidateSharingPolicyMutationResponse(
                (await ReadRequiredJsonAsync<DataSharingPolicyDto>(
                    resp,
                    token,
                    "연동 정책 생성(tenant-settings/sharing-policies)"))!,
                expectedPolicyId: null,
                request),
            ct);
    }

    public async Task<DataSharingPolicyDto?> UpdateSharingPolicyAsync(Guid policyId, UpsertDataSharingPolicyRequest request, CancellationToken ct = default)
    {
        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "연동 정책 저장(tenant-settings/sharing-policies)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.PutAsJsonAsync($"tenant-settings/sharing-policies/{policyId}", request, token);
            },
            readAsync: async (resp, token) => ValidateSharingPolicyMutationResponse(
                (await ReadRequiredJsonAsync<DataSharingPolicyDto>(
                    resp,
                    token,
                    "연동 정책 저장(tenant-settings/sharing-policies)"))!,
                policyId,
                request),
            ct);
    }

    public async Task DeleteSharingPolicyAsync(Guid policyId, long? expectedRevision = null, CancellationToken ct = default)
    {
        await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "연동 정책 삭제(tenant-settings/sharing-policies)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: false);
                return await _http.DeleteAsync(WithExpectedRevision($"tenant-settings/sharing-policies/{policyId}", expectedRevision), token);
            },
            readAsync: async (resp, token) => ValidateDeletedSharingPolicyResponse(
                (await ReadRequiredJsonAsync<DataSharingPolicyDto>(
                    resp,
                    token,
                    "연동 정책 삭제(tenant-settings/sharing-policies)"))!,
                policyId,
                expectedRevision),
            ct);
    }

    // ── Sync ──────────────────────────────────────────────────────────────────
    public async Task<SyncPullResponse?> PullAsync(long sinceRevision, CancellationToken ct = default)
        => await PullAsync(sinceRevision, businessDatabaseNameOverride: null, ct);

    public async Task<SyncPullResponse?> PullAsync(
        long sinceRevision,
        string? businessDatabaseNameOverride,
        CancellationToken ct = default)
        => await PullAsync(
            sinceRevision,
            businessDatabaseNameOverride,
            rentalAdministrationOnly: false,
            ct);

    public async Task<SyncPullResponse?> PullAsync(
        long sinceRevision,
        string? businessDatabaseNameOverride,
        bool rentalAdministrationOnly,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "동기화 다운로드(sync/pull)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
                var rentalAdministrationQuery = rentalAdministrationOnly
                    ? "&rentalAdministrationOnly=true"
                    : string.Empty;
                return await _http.GetAsync($"sync/pull?sinceRev={sinceRevision}{rentalAdministrationQuery}", token);
            },
            readAsync: static (resp, token) => ReadRequiredJsonAsync<SyncPullResponse>(
                resp,
                token,
                "동기화 다운로드(sync/pull)"),
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
            readAsync: static (resp, token) => ReadRequiredJsonAsync<SyncPushResult>(
                resp,
                token,
                "동기화 업로드(sync/push)"),
            ct);
    }

    public async Task<ItemDuplicateMergePreviewDto?> PreviewItemDuplicateMergeAsync(
        ItemDuplicateMergePreviewRequestDto request,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "품목 중복 병합 사전검증(items/duplicate-merge/preview)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.PostAsJsonAsync("items/duplicate-merge/preview", request, token);
            },
            readAsync: static (resp, token) => ReadRequiredJsonAsync<ItemDuplicateMergePreviewDto>(
                resp,
                token,
                "품목 중복 병합 사전검증(items/duplicate-merge/preview)"),
            ct);
    }

    public async Task<ItemDuplicateMergeResultDto?> ExecuteItemDuplicateMergeAsync(
        ItemDuplicateMergeRequestDto request,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "품목 중복 병합(items/duplicate-merge)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true);
                return await _http.PostAsJsonAsync("items/duplicate-merge", request, token);
            },
            readAsync: static (resp, token) => ReadRequiredJsonAsync<ItemDuplicateMergeResultDto>(
                resp,
                token,
                "품목 중복 병합(items/duplicate-merge)"),
            ct,
            preserveAmbiguousDispatch: true);
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
            readAsync: static (resp, token) => ReadRequiredJsonAsync<SyncStatusDto>(
                resp,
                token,
                "동기화 상태 조회(sync/status)"),
            ct);
    }

    public async Task<SyncStatusDto?> WaitForSyncChangeAsync(
        long sinceRevision,
        TimeSpan timeout,
        string? businessDatabaseNameOverride = null,
        CancellationToken ct = default)
    {
        var timeoutSeconds = Math.Clamp((int)Math.Ceiling(timeout.TotalSeconds), 1, 30);
        var query = BuildQuery(
            "sync/wait",
            ("sinceRev", Math.Max(0, sinceRevision).ToString()),
            ("timeoutSeconds", timeoutSeconds.ToString()));

        return await ExecuteWithRetryAsync(
            operationName: "실시간 변경 대기(sync/wait)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
                return await _http.GetAsync(query, token);
            },
            readAsync: static (resp, token) => ReadRequiredJsonAsync<SyncStatusDto>(
                resp,
                token,
                "실시간 변경 대기(sync/wait)"),
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

    public async Task<byte[]> DownloadCustomerContractContentAsync(Guid contractId, CancellationToken ct = default)
    {
        if (contractId == Guid.Empty)
            throw new ArgumentException("계약서 ID가 비어 있습니다.", nameof(contractId));

        return await ExecuteWithRetryAsync(
                   operationName: "거래처 계약서 파일 다운로드(customers/contracts/content)",
                   sendAsync: async token =>
                   {
                       SetAuthHeader(includeBusinessDatabaseHeader: true);
                       return await _http.GetAsync($"customers/contracts/{contractId:D}/content", token);
                   },
                   readAsync: static async (resp, token) => await resp.Content.ReadAsByteArrayAsync(token),
                   ct)
               ?? [];
    }

    public async Task<byte[]> DownloadPaymentAttachmentContentAsync(Guid attachmentId, CancellationToken ct = default)
    {
        if (attachmentId == Guid.Empty)
            throw new ArgumentException("첨부 파일 ID가 비어 있습니다.", nameof(attachmentId));

        return await ExecuteWithRetryAsync(
                   operationName: "입금 첨부 파일 다운로드(payments/attachments/content)",
                   sendAsync: async token =>
                   {
                       SetAuthHeader(includeBusinessDatabaseHeader: true);
                       return await _http.GetAsync($"payments/attachments/{attachmentId:D}/content", token);
                   },
                   readAsync: static async (resp, token) => await resp.Content.ReadAsByteArrayAsync(token),
                   ct)
               ?? [];
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
        => await RestoreRecycleBinAsync(items, businessDatabaseNameOverride: null, ct);

    public async Task<RecycleBinMutationResultDto?> RestoreRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        string? businessDatabaseNameOverride,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var requestItems = items
            .Select(item => new RecycleBinMutationTargetDto
            {
                EntityId = item.EntityId,
                Kind = item.Kind,
                ExpectedRevision = item.ExpectedRevision
            })
            .ToList();

        return await ExecuteNonIdempotentSingleDispatchAsync(
            operationName: "휴지통 복원(recycle-bin/restore)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
                return await _http.PostAsJsonAsync(
                    "recycle-bin/restore",
                    new RecycleBinMutationRequest { Items = requestItems },
                    token);
            },
            readAsync: async (resp, token) =>
            {
                var result = await resp.Content.ReadFromJsonAsync<RecycleBinMutationResultDto>(token);
                EnsureDefinitiveRecycleBinRestoreReceipt(requestItems, result);
                return result;
            },
            ct);
    }

    private static void EnsureDefinitiveRecycleBinRestoreReceipt(
        IReadOnlyList<RecycleBinMutationTargetDto> requestItems,
        RecycleBinMutationResultDto? result)
    {
        if (result is null ||
            result.Results is null ||
            result.Messages is null)
        {
            throw new InvalidDataException(
                "휴지통 복원 응답의 항목별 처리 결과 구조가 비어 있습니다.");
        }

        var expectedKeys = new HashSet<(Guid EntityId, string Kind)>();
        foreach (var requestItem in requestItems)
        {
            var key = (
                EntityId: requestItem.EntityId,
                Kind: NormalizeRecycleBinMutationKind(requestItem.Kind));
            if (key.EntityId == Guid.Empty ||
                string.IsNullOrWhiteSpace(key.Kind) ||
                !expectedKeys.Add(key))
            {
                throw new InvalidDataException(
                    "휴지통 복원 요청의 항목 식별자가 유효하거나 고유하지 않습니다.");
            }
        }

        if (result.RequestedCount != requestItems.Count ||
            result.Results.Count != requestItems.Count)
        {
            throw new InvalidDataException(
                "휴지통 복원 응답의 요청/항목별 처리 건수가 일치하지 않습니다.");
        }

        var reportedKeys = new HashSet<(Guid EntityId, string Kind)>();
        var reportedSuccessCount = 0;
        foreach (var itemResult in result.Results)
        {
            if (itemResult is null)
            {
                throw new InvalidDataException(
                    "휴지통 복원 응답에 비어 있는 항목별 처리 결과가 포함되어 있습니다.");
            }

            var key = (
                EntityId: itemResult.EntityId,
                Kind: NormalizeRecycleBinMutationKind(itemResult.Kind));
            if (key.EntityId == Guid.Empty ||
                string.IsNullOrWhiteSpace(key.Kind) ||
                itemResult.Message is null ||
                !reportedKeys.Add(key))
            {
                throw new InvalidDataException(
                    "휴지통 복원 응답의 항목 식별자가 유효하거나 고유하지 않습니다.");
            }

            if (itemResult.Success)
                reportedSuccessCount++;
        }

        if (!reportedKeys.SetEquals(expectedKeys) ||
            result.SucceededCount != reportedSuccessCount)
        {
            throw new InvalidDataException(
                "휴지통 복원 응답의 항목별 처리 결과가 요청 대상 또는 성공 건수와 일치하지 않습니다.");
        }
    }

    private static string NormalizeRecycleBinMutationKind(string? kind)
        => (kind ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "customer" => "customer",
            "contract" => "contract",
            "item" => "item",
            "companyprofile" or "company-profile" => "company-profile",
            "customercategory" or "customer-category" => "customer-category",
            "pricegradeoption" or "price-grade-option" => "price-grade-option",
            "tradetypeoption" or "trade-type-option" => "trade-type-option",
            "itemcategoryoption" or "item-category-option" => "item-category-option",
            "invoice" => "invoice",
            "payment" => "payment",
            "transaction" => "transaction",
            "inventorytransfer" or "inventory-transfer" => "inventory-transfer",
            "rentalmanagementcompany" or "rental-management-company" => "rental-management-company",
            "rentalbillingprofile" or "rental-billing-profile" => "rental-billing-profile",
            "rentalasset" or "rental-asset" => "rental-asset",
            "rentalbillinglog" or "rental-billing-log" => "rental-billing-log",
            _ => string.Empty
        };

    public async Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        CancellationToken ct = default)
        => await PurgeRecycleBinAsync(items, businessDatabaseNameOverride: null, ct);

    public async Task<RecycleBinMutationResultDto?> PurgeRecycleBinAsync(
        IReadOnlyList<RecycleBinMutationTargetDto> items,
        string? businessDatabaseNameOverride,
        CancellationToken ct = default)
    {
        return await ExecuteWithRetryAsync(
            operationName: "휴지통 영구삭제(recycle-bin/purge)",
            sendAsync: async token =>
            {
                SetAuthHeader(includeBusinessDatabaseHeader: true, businessDatabaseNameOverride);
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
        if (ex is AggregateException aggregate)
            return aggregate.InnerExceptions.Any(inner => IsTransient(inner, ct));

        if (IsTransientSingle(ex, ct))
            return true;

        return ex.InnerException is not null && IsTransient(ex.InnerException, ct);
    }

    private static bool IsTransientSingle(Exception ex, CancellationToken ct)
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
        return BuildFailureMessage(response, body);
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

    private async Task EnsureFreshTokenAsync(CancellationToken ct)
    {
        if (!_session.ShouldRefreshToken(TokenRefreshLeadTime) || IsSessionRefreshFailureInCooldown())
            return;

        await _sessionRefreshLock.WaitAsync(ct);
        try
        {
            if (!_session.ShouldRefreshToken(TokenRefreshLeadTime) || IsSessionRefreshFailureInCooldown())
                return;

            var refreshed = await RefreshSessionAsync(ct);
            if (await TryApplyRefreshedSessionAsync(refreshed))
            {
                _lastSessionRefreshFailureAtUtc = DateTime.MinValue;
                AppLogger.Info("AUTH", $"로그인 세션 자동 갱신 완료: 만료 예정 {FormatTokenExpiryForLog()}");
                return;
            }

            await ClearSessionAfterRejectedRefreshAsync(
                "로그인 세션 자동 갱신이 서버에서 거부되었습니다. 세션 만료 또는 권한/담당지점/사업 범위 변경으로 다시 로그인이 필요합니다.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            throw;
        }
        catch (LocalStateService.AuthenticationCachePersistenceException ex)
            when (!ex.OfflineFallbackBlocked)
        {
            _session.Clear();
            throw;
        }
        catch (Exception ex)
        {
            MarkSessionRefreshFailure($"로그인 세션 자동 갱신 실패: {ex.Message}");
        }
        finally
        {
            _sessionRefreshLock.Release();
        }
    }

    private async Task<bool> TryRefreshSessionAfterUnauthorizedAsync(CancellationToken ct)
    {
        if (!_session.IsLoggedIn || _session.IsOfflineMode || string.IsNullOrWhiteSpace(_session.Token))
            return false;

        await _sessionRefreshLock.WaitAsync(ct);
        try
        {
            var refreshed = await RefreshSessionAsync(ct);
            if (await TryApplyRefreshedSessionAsync(refreshed))
            {
                _lastSessionRefreshFailureAtUtc = DateTime.MinValue;
                AppLogger.Info("AUTH", $"401 응답 후 로그인 세션 갱신 완료: 만료 예정 {FormatTokenExpiryForLog()}");
                return true;
            }

            await ClearSessionAfterRejectedRefreshAsync(
                "401 응답 후 로그인 세션 갱신이 서버에서 거부되었습니다. 세션 만료 또는 권한/담당지점/사업 범위 변경으로 다시 로그인이 필요합니다.");
            return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            throw;
        }
        catch (LocalStateService.AuthenticationCachePersistenceException ex)
            when (!ex.OfflineFallbackBlocked)
        {
            _session.Clear();
            throw;
        }
        catch (Exception ex)
        {
            MarkSessionRefreshFailure($"401 응답 후 로그인 세션 갱신 실패: {ex.Message}");
            return false;
        }
        finally
        {
            _sessionRefreshLock.Release();
        }
    }

    private async Task<bool> TryApplyRefreshedSessionAsync(LoginResponse? response)
    {
        if (response is null || string.IsNullOrWhiteSpace(response.Token) || response.User is null)
            return false;

        var previousUsername = _session.User?.Username;
        if (string.IsNullOrWhiteSpace(previousUsername))
            return false;

        if (!string.Equals(
                previousUsername.Trim(),
                response.User.Username?.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            LocalStateService.AuthenticationCachePersistenceException? fatalFailure = null;
            if (_localState is not null)
            {
                foreach (var username in new[] { previousUsername, response.User.Username }
                             .Where(username => !string.IsNullOrWhiteSpace(username))
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _localState.RevokeRejectedAuthenticationCacheAsync(
                            username,
                            officeCode: null,
                            CancellationToken.None);
                    }
                    catch (LocalStateService.AuthenticationCachePersistenceException ex)
                        when (ex.OfflineFallbackBlocked)
                    {
                        AppLogger.Error(
                            "AUTH",
                            $"세션 사용자 불일치 계정의 캐시 데이터 제거는 실패했지만 오프라인 인증은 차단했습니다: {username}",
                            ex);
                    }
                    catch (LocalStateService.AuthenticationCachePersistenceException ex)
                    {
                        fatalFailure ??= ex;
                        AppLogger.Error(
                            "AUTH",
                            $"세션 사용자 불일치 계정의 영속 오프라인 인증 차단이 완전히 실패했습니다: {username}",
                            ex);
                    }
                    catch (Exception ex)
                    {
                        AppLogger.Error(
                            "AUTH",
                            $"세션 사용자 불일치 계정의 오프라인 인증 차단에 실패했습니다: {username}",
                            ex);
                    }
                }
            }

            _session.Clear();
            AppLogger.Warn(
                "AUTH",
                "세션 갱신 응답의 사용자가 기존 로그인 사용자와 달라 응답을 거부하고 두 사용자 캐시를 폐기했습니다.");
            if (fatalFailure is not null)
                throw fatalFailure;
            return false;
        }

        if (_localState is not null && !string.IsNullOrWhiteSpace(previousUsername))
        {
            try
            {
                await _localState.RefreshCachedSessionAfterOnlineValidationAsync(
                    previousUsername,
                    response.User,
                    CancellationToken.None);
            }
            catch (LocalStateService.AuthenticationCachePersistenceException ex)
                when (ex.OfflineFallbackBlocked)
            {
                AppLogger.Error(
                    "AUTH",
                    "갱신된 권한의 캐시 데이터 저장은 실패했지만 오프라인 인증은 차단되어 온라인 세션을 계속합니다.",
                    ex);
            }
            catch (LocalStateService.AuthenticationCachePersistenceException)
            {
                _session.Clear();
                throw;
            }
            catch (Exception ex)
            {
                _session.Clear();
                AppLogger.Error(
                    "AUTH",
                    "갱신된 로그인 권한의 오프라인 차단 상태를 보장할 수 없어 온라인 세션도 해제했습니다.",
                    ex);
                return false;
            }
        }

        _session.RefreshSession(response.Token, response.User, response.ExpiresAtUtc);
        return true;
    }

    private bool IsSessionRefreshFailureInCooldown()
        => _lastSessionRefreshFailureAtUtc != DateTime.MinValue
           && DateTime.UtcNow - _lastSessionRefreshFailureAtUtc < TokenRefreshFailureCooldown;

    private void MarkSessionRefreshFailure(string message)
    {
        _lastSessionRefreshFailureAtUtc = DateTime.UtcNow;
        AppLogger.Warn("AUTH", message);
    }

    private async Task ClearSessionAfterRejectedRefreshAsync(string message)
    {
        var username = _session.User?.Username;
        var officeCode = _session.User?.OfficeCode;
        _lastSessionRefreshFailureAtUtc = DateTime.MinValue;
        _session.Clear();

        if (_localState is not null && !string.IsNullOrWhiteSpace(username))
        {
            try
            {
                await _localState.RevokeRejectedAuthenticationCacheAsync(
                    username,
                    officeCode,
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppLogger.Error(
                    "AUTH",
                    "서버에서 거부된 로그인 세션의 오프라인 인증 캐시 제거에 실패했습니다.",
                    ex);
            }
        }

        AppLogger.Warn(
            "AUTH",
            string.IsNullOrWhiteSpace(username)
                ? $"{message} 기존 로그인 세션을 해제했습니다."
                : $"{message} 기존 로그인 세션을 해제했습니다. 사용자: {username}");
    }

    private string FormatTokenExpiryForLog()
        => _session.TokenExpiresAtUtc is null
            ? "알 수 없음"
            : $"{_session.TokenExpiresAtUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";

    private async Task<T?> ExecuteNonIdempotentSingleDispatchAsync<T>(
        string operationName,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<HttpResponseMessage, CancellationToken, Task<T?>> readAsync,
        CancellationToken ct)
    {
        await EnsureFreshTokenAsync(ct);
        ct.ThrowIfCancellationRequested();

        using var timeoutCts = CreateOperationTimeoutTokenSource(operationName, ct);
        HttpResponseMessage response;
        try
        {
            response = await sendAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DesktopClientUpgradeRequiredException)
        {
            throw;
        }
        catch (ExpectedRevisionConflictException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new AmbiguousMutationOutcomeException(operationName, ex);
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                try
                {
                    return await readAsync(response, timeoutCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (AmbiguousMutationOutcomeException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    throw new AmbiguousMutationOutcomeException(
                        operationName,
                        ex,
                        response.StatusCode);
                }
            }

            if (ShouldRetry(response.StatusCode))
            {
                string detail;
                try
                {
                    detail = await BuildFailureMessageAsync(response, timeoutCts.Token);
                }
                catch (Exception ex)
                {
                    detail = $"응답 본문을 확인하지 못했습니다: {ex.Message}";
                }

                throw new AmbiguousMutationOutcomeException(
                    operationName,
                    new HttpRequestException(
                        $"HTTP {((int)response.StatusCode)}: {detail}",
                        inner: null,
                        response.StatusCode),
                    response.StatusCode);
            }

            Exception definitiveFailure;
            try
            {
                definitiveFailure = await CreateFailureExceptionAsync(
                    operationName,
                    response,
                    timeoutCts.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                definitiveFailure = new HttpRequestException(
                    $"{operationName} 실패: HTTP {((int)response.StatusCode)} 응답을 받았지만 본문을 확인하지 못했습니다.",
                    ex,
                    response.StatusCode);
            }

            ExceptionDispatchInfo.Capture(definitiveFailure).Throw();
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private static TenantDefinitionDto ValidateTenantMutationResponse(
        TenantDefinitionDto response,
        string canonicalTenantCode,
        UpdateTenantDefinitionRequest request)
    {
        var expectedDisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? TenantScopeCatalog.GetTenantDisplayName(canonicalTenantCode)
            : request.DisplayName.Trim();
        if (response.Id == Guid.Empty ||
            !string.Equals(response.TenantCode, canonicalTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.DisplayName, expectedDisplayName, StringComparison.Ordinal) ||
            !string.Equals(
                TenantScopeCatalog.NormalizeStorageModeOrDefault(response.StorageMode),
                TenantScopeCatalog.NormalizeStorageModeOrDefault(request.StorageMode),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.Description, request.Description?.Trim() ?? string.Empty, StringComparison.Ordinal) ||
            response.IsActive != request.IsActive ||
            response.IsDeleted == request.IsActive ||
            response.Revision <= Math.Max(0, request.ExpectedRevision))
        {
            throw new InvalidDataException("업체권역 저장 응답의 키, 필드 또는 리비전이 요청과 일치하지 않습니다.");
        }

        return response;
    }

    private static TenantOfficeDefinitionDto ValidateOfficeMutationResponse(
        TenantOfficeDefinitionDto response,
        string canonicalOfficeCode,
        UpdateTenantOfficeDefinitionRequest request)
    {
        var expectedTenantCode = TenantScopeCatalog.GetTenantCodeForOffice(canonicalOfficeCode);
        var expectedDisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? OfficeCodeCatalog.GetOfficeDisplayName(canonicalOfficeCode)
            : request.DisplayName.Trim();
        if (response.Id == Guid.Empty ||
            !string.Equals(response.OfficeCode, canonicalOfficeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.TenantCode, expectedTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.DisplayName, expectedDisplayName, StringComparison.Ordinal) ||
            response.IsHeadOffice != request.IsHeadOffice ||
            response.IsActive != request.IsActive ||
            response.IsDeleted == request.IsActive ||
            response.Revision <= Math.Max(0, request.ExpectedRevision))
        {
            throw new InvalidDataException("지점 정의 저장 응답의 키, 범위, 필드 또는 리비전이 요청과 일치하지 않습니다.");
        }

        return response;
    }

    private static DataSharingPolicyDto ValidateSharingPolicyMutationResponse(
        DataSharingPolicyDto response,
        Guid? expectedPolicyId,
        UpsertDataSharingPolicyRequest request)
    {
        var sourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(request.SourceOfficeCode);
        var targetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(request.TargetOfficeCode);
        var sourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            request.SourceTenantCode,
            sourceOfficeCode);
        var targetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            request.TargetTenantCode,
            targetOfficeCode);
        if (response.Id == Guid.Empty ||
            (expectedPolicyId.HasValue && response.Id != expectedPolicyId.Value) ||
            !string.Equals(response.SourceTenantCode, sourceTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.SourceOfficeCode, sourceOfficeCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.TargetTenantCode, targetTenantCode, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(response.TargetOfficeCode, targetOfficeCode, StringComparison.OrdinalIgnoreCase) ||
            response.ShareCustomers != request.ShareCustomers ||
            response.ShareItems != request.ShareItems ||
            response.ShareInvoices != request.ShareInvoices ||
            response.SharePayments != request.SharePayments ||
            response.ShareContracts != request.ShareContracts ||
            response.ShareReports != request.ShareReports ||
            response.ShareRentals != request.ShareRentals ||
            response.ShareDeliveries != request.ShareDeliveries ||
            response.AllowTargetWrite != request.AllowTargetWrite ||
            response.IsActive != request.IsActive ||
            response.IsDeleted == request.IsActive ||
            !string.Equals(response.Note, request.Note?.Trim() ?? string.Empty, StringComparison.Ordinal) ||
            response.Revision <= Math.Max(0, request.ExpectedRevision))
        {
            throw new InvalidDataException("연동 정책 저장 응답의 키, 범위, 필드 또는 리비전이 요청과 일치하지 않습니다.");
        }

        return response;
    }

    private static object ValidateDeletedSharingPolicyResponse(
        DataSharingPolicyDto response,
        Guid expectedPolicyId,
        long? expectedRevision)
    {
        if (response.Id != expectedPolicyId ||
            response.IsActive ||
            !response.IsDeleted ||
            response.Revision <= Math.Max(0, expectedRevision ?? 0))
        {
            throw new InvalidDataException("연동 정책 삭제 응답의 키, 상태 또는 리비전이 요청과 일치하지 않습니다.");
        }

        return response;
    }

    private async Task<T?> ExecuteWithRetryAsync<T>(
        string operationName,
        Func<CancellationToken, Task<HttpResponseMessage>> sendAsync,
        Func<HttpResponseMessage, CancellationToken, Task<T?>> readAsync,
        CancellationToken ct,
        bool preserveAmbiguousDispatch = false)
    {
        await EnsureFreshTokenAsync(ct);

        Exception? lastException = null;
        Exception? ambiguousDispatchException = null;
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

                if (response.StatusCode == HttpStatusCode.Unauthorized &&
                    attempt < MaxRetryCount &&
                    await TryRefreshSessionAfterUnauthorizedAsync(ct))
                {
                    AppLogger.Info("AUTH", $"{operationName} 401 응답 후 새 로그인 세션으로 재시도합니다.");
                    continue;
                }

                if (response.StatusCode == HttpStatusCode.UpgradeRequired)
                {
                    throw await CreateFailureExceptionAsync(
                        operationName,
                        response,
                        timeoutCts.Token);
                }

                var message = await BuildFailureMessageAsync(response, timeoutCts.Token);
                if (preserveAmbiguousDispatch && IsAmbiguousMutationResponse(response.StatusCode))
                {
                    ambiguousDispatchException ??= new HttpRequestException(
                        $"{operationName} 응답 {((int)response.StatusCode)} 이후 서버 반영 여부를 확정할 수 없습니다: {message}",
                        inner: null,
                        response.StatusCode);
                }
                var retryable = ShouldRetry(response.StatusCode) && attempt < MaxRetryCount;
                if (!retryable)
                    throw await CreateFailureExceptionAsync(operationName, response, timeoutCts.Token);

                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (OperationCanceledException) when (
                ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < MaxRetryCount)
            {
                lastException = ex;
                if (preserveAmbiguousDispatch && IsAmbiguousDispatchFailure(ex, ct))
                    ambiguousDispatchException ??= ex;
                AppLogger.Warn("API", $"{operationName} 재시도 {attempt}/{MaxRetryCount}: {ex.Message}");
                await Task.Delay(delay, ct);
                delay += delay;
            }
            catch (Exception ex)
            {
                lastException = ex;
                if (preserveAmbiguousDispatch && IsAmbiguousDispatchFailure(ex, ct))
                    ambiguousDispatchException ??= ex;
                break;
            }
        }

        if (ambiguousDispatchException is not null)
        {
            var history = ReferenceEquals(ambiguousDispatchException, lastException) || lastException is null
                ? ambiguousDispatchException
                : new AggregateException(ambiguousDispatchException, lastException);
            throw new HttpRequestException(
                $"{operationName} 실패: 요청 전송 후 응답을 확인하지 못한 이력이 있어 완료 여부를 확정할 수 없습니다. 마지막 오류: {lastException?.Message}",
                history,
                statusCode: null);
        }

        if (lastException is
            ExpectedRevisionConflictException or
            DesktopClientUpgradeRequiredException)
            ExceptionDispatchInfo.Capture(lastException).Throw();

        throw new HttpRequestException(
            $"{operationName} 실패 (최대 재시도 {MaxRetryCount}회): {lastException?.Message}",
            lastException,
            ResolveHttpStatusCode(lastException));
    }

    private static bool IsAmbiguousDispatchFailure(Exception exception, CancellationToken ct)
        => ResolveHttpStatusCode(exception) is null && IsTransient(exception, ct);

    private static bool IsAmbiguousMutationResponse(HttpStatusCode statusCode)
        => statusCode is
            HttpStatusCode.InternalServerError or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static HttpStatusCode? ResolveHttpStatusCode(Exception? exception)
    {
        while (exception is not null)
        {
            if (exception is HttpRequestException { StatusCode: { } statusCode })
                return statusCode;

            exception = exception.InnerException;
        }

        return null;
    }

    private static async Task<T?> ReadRequiredJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken ct,
        string operationName)
        where T : class
    {
        try
        {
            var payload = await response.Content.ReadFromJsonAsync<T>(ct);
            if (payload is not null)
                return payload;
        }
        catch (JsonException ex)
        {
            throw new HttpRequestException(
                $"{operationName} 실패: 서버 응답 본문을 해석할 수 없습니다.",
                ex);
        }

        throw new HttpRequestException($"{operationName} 실패: 서버 응답 본문이 비어 있습니다.");
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

        if (operationName.Contains("파일 다운로드", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromMinutes(2);

        if (operationName.Contains("실시간 변경 대기(sync/wait)", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromSeconds(45);

        return TimeSpan.FromSeconds(30);
    }

    private async Task<Exception> CreateFailureExceptionAsync(string operationName, HttpResponseMessage response, CancellationToken ct)
    {
        if (response.StatusCode == HttpStatusCode.UpgradeRequired)
        {
            var exception =
                await DesktopUpgradeRequiredResponseParser
                .CreateExceptionAsync(
                    response.RequestMessage?.RequestUri?.PathAndQuery ??
                    operationName,
                    response.Content,
                    ct);
            if (_upgradeObserver is not null)
            {
                try
                {
                    await _upgradeObserver.ObserveAsync(
                        exception,
                        CancellationToken.None);
                }
                catch (Exception observerException)
                {
                    AppLogger.Error(
                        "UPDATE",
                        "Desktop 426 observer failed; preserving the original typed exception.",
                        observerException);
                }
            }

            return exception;
        }

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
        => ApiErrorMessageFormatter.BuildFailureMessage(response.StatusCode, response.ReasonPhrase, body);
}
