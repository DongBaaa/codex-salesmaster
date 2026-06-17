using System.Net;
using System.Text.Json;
using GeoraePlan.Mobile.App.Models;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class SyncCoordinator
{
    private readonly JsonSyncStateStore _store;
    private readonly GeoraePlanApiClient _api;
    private readonly PaymentAttachmentDraftStore _attachmentStore;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    public const string ConcurrencyConflictUserMessage = "다른 PC/모바일에서 먼저 수정되어 최신값을 다시 불러왔습니다. 내용을 확인한 뒤 다시 저장해 주세요.";

    public SyncCoordinator(JsonSyncStateStore store, GeoraePlanApiClient api, PaymentAttachmentDraftStore attachmentStore)
    {
        _store = store;
        _api = api;
        _attachmentStore = attachmentStore;
    }

    public static bool IsConcurrencyConflictState(MobileSyncState state)
        => state is not null && IsConcurrencyConflictMessage(state.LastError);

    public static bool IsConcurrencyConflictMessage(string? message)
        => !string.IsNullOrWhiteSpace(message) &&
           message.Contains("다른 PC/모바일에서 먼저 수정", StringComparison.Ordinal);

    public Task<MobileSyncState> LoadAsync(CancellationToken ct = default)
        => _store.LoadAsync(ct);

    public async Task<MobileSyncState> PullAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.LastAttemptUtc = DateTime.UtcNow;

            try
            {
                var response = await _api.PullAsync(state.LastRevision, ct);
                if (response is not null)
                    ApplyPullResponse(state, response);
            }
            catch (Exception ex)
            {
                MarkFailure(state, ex);
            }

            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> PushAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            return await PushInternalAsync(state, ct);
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> SynchronizeNowAsync(CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state = await PushInternalAsync(state, ct);
            if (!string.IsNullOrWhiteSpace(state.LastError))
                return state;

            state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct);
            if (!string.IsNullOrWhiteSpace(state.LastError))
            {
                await _store.SaveAsync(state, ct);
                return state;
            }

            state = await PullInternalAsync(state, ct);
            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> RefreshIfServerChangedAsync(string reason, TimeSpan minInterval, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            var now = DateTime.UtcNow;
            if (state.LastBackgroundSyncUtc.HasValue && now - state.LastBackgroundSyncUtc.Value < minInterval)
                return state;

            state.LastBackgroundSyncUtc = now;

            if (HasPendingServerSyncPayload(state) || state.PendingPaymentAttachments.Count > 0)
            {
                state = await PushInternalAsync(state, ct);
                if (string.IsNullOrWhiteSpace(state.LastError))
                    state = await UploadPendingPaymentAttachmentsInternalAsync(state, ct);
                if (string.IsNullOrWhiteSpace(state.LastError))
                    state = await PullInternalAsync(state, ct);

                await _store.SaveAsync(state, ct);
                return state;
            }

            try
            {
                var syncStatus = await _api.GetSyncStatusAsync(ct);
                if (syncStatus is not null && syncStatus.CurrentServerRevision > state.LastRevision)
                    state = await PullInternalAsync(state, ct);
                else
                {
                    state.LastSuccessUtc ??= now;
                    state.LastError = string.Empty;
                }
            }
            catch (Exception ex)
            {
                MarkFailure(state, ex);
            }

            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> SaveInvoiceImmediatelyAsync(InvoiceDto invoice, CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.LastAttemptUtc = DateTime.UtcNow;
            state.Normalize();
            state.PendingPush.Invoices.RemoveAll(x => x.Id == invoice.Id);

            try
            {
                var isExistingInvoice = invoice.Revision > 0 || !string.IsNullOrWhiteSpace(invoice.InvoiceNumber);
                var saved = isExistingInvoice
                    ? await _api.UpdateInvoiceAsync(invoice, ct)
                    : await _api.CreateInvoiceAsync(invoice, ct);
                if (saved is not null)
                    state.LastRevision = Math.Max(state.LastRevision, saved.Revision);

                state.LastSuccessUtc = DateTime.UtcNow;
                state.LastError = string.Empty;
                state.ConsecutiveFailureCount = 0;
                state = await PullInternalAsync(state, ct);
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex))
            {
                await MarkConcurrencyConflictAndRefreshAsync(state, ex, ct);
            }
            catch (Exception ex) when (IsNonRetryableClientFailure(ex))
            {
                MarkFailure(state, ex);
            }
            catch (Exception ex)
            {
                state.PendingPush.Invoices.Add(invoice);
                MarkFailure(state, ex);
            }

            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> SavePaymentImmediatelyAsync(
        PaymentDto payment,
        IEnumerable<PendingPaymentAttachmentRecord>? attachments = null,
        TransactionDto? linkedTransaction = null,
        CancellationToken ct = default)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.LastAttemptUtc = DateTime.UtcNow;
            state.Normalize();
            state.PendingPush.Payments.RemoveAll(x => x.Id == payment.Id);
            if (linkedTransaction is not null)
                state.PendingPush.Transactions.RemoveAll(x => x.Id == linkedTransaction.Id);

            var attachmentList = attachments?.ToList() ?? [];

            try
            {
                if (linkedTransaction is null)
                {
                    var saved = await _api.CreatePaymentAsync(payment, ct);
                    if (saved is not null)
                        state.LastRevision = Math.Max(state.LastRevision, saved.Revision);
                }
                else
                {
                    var request = new SyncPushRequest { DeviceId = state.DeviceId };
                    request.Transactions.Add(linkedTransaction);
                    request.Payments.Add(payment);
                    var result = await _api.PushAsync(request, ct);
                    if (result is not null)
                    {
                        state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);
                        if (result.ConflictCount > 0)
                        {
                            await MarkPushConflictAndRefreshAsync(state, result, ct);
                            await _store.SaveAsync(state, ct);
                            return state;
                        }
                    }
                }

                foreach (var attachment in attachmentList)
                {
                    attachment.PaymentId = payment.Id;
                    state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);

                    try
                    {
                        await _api.UploadPaymentAttachmentAsync(payment.Id, attachment, ct);
                        await _attachmentStore.RemoveAsync(attachment, ct);
                    }
                    catch (Exception uploadEx)
                    {
                        state.PendingPaymentAttachments.Add(attachment);
                        if (string.IsNullOrWhiteSpace(state.LastError))
                            state.LastError = uploadEx.Message;
                    }
                }

                state.LastSuccessUtc = DateTime.UtcNow;
                state.ConsecutiveFailureCount = 0;
                state = await PullInternalAsync(state, ct);
            }
            catch (Exception ex) when (IsConcurrencyConflict(ex))
            {
                await MarkConcurrencyConflictAndRefreshAsync(state, ex, ct);
            }
            catch (Exception ex) when (IsNonRetryableClientFailure(ex))
            {
                MarkFailure(state, ex);
            }
            catch (Exception ex)
            {
                state.PendingPush.Payments.Add(payment);
                if (linkedTransaction is not null)
                    state.PendingPush.Transactions.Add(linkedTransaction);
                foreach (var attachment in attachmentList)
                {
                    attachment.PaymentId = payment.Id;
                    state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);
                    state.PendingPaymentAttachments.Add(attachment);
                }

                MarkFailure(state, ex);
            }

            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    public async Task<MobileSyncState> TryBackgroundSyncAsync(string reason, TimeSpan minInterval, CancellationToken ct = default)
        => await RefreshIfServerChangedAsync(reason, minInterval, ct);

    public async Task<MobileSyncState> QueueInvoiceDraftAsync(InvoiceDto invoice, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.Invoices.RemoveAll(x => x.Id == invoice.Id);
            state.PendingPush.Invoices.Add(invoice);
        }, ct);

    public async Task<MobileSyncState> QueuePaymentDraftAsync(PaymentDto payment, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.Payments.RemoveAll(x => x.Id == payment.Id);
            state.PendingPush.Payments.Add(payment);
        }, ct);

    public async Task<MobileSyncState> QueueTransactionDraftAsync(TransactionDto transaction, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.Transactions.RemoveAll(x => x.Id == transaction.Id);
            state.PendingPush.Transactions.Add(transaction);
        }, ct);

    public async Task<MobileSyncState> QueueTransactionAttachmentDraftAsync(TransactionAttachmentDto attachment, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.TransactionAttachments.RemoveAll(x => x.Id == attachment.Id);
            state.PendingPush.TransactionAttachments.Add(attachment);
        }, ct);

    public async Task<MobileSyncState> QueueInventoryTransferDraftAsync(InventoryTransferDto transfer, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.InventoryTransfers.RemoveAll(x => x.Id == transfer.Id);
            state.PendingPush.InventoryTransfers.Add(transfer);
        }, ct);

    public async Task<MobileSyncState> QueueRentalManagementCompanyDraftAsync(RentalManagementCompanyDto company, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalManagementCompanies.RemoveAll(x => x.Id == company.Id);
            state.PendingPush.RentalManagementCompanies.Add(company);
        }, ct);

    public async Task<MobileSyncState> QueueRentalBillingProfileDraftAsync(RentalBillingProfileDto profile, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalBillingProfiles.RemoveAll(x => x.Id == profile.Id);
            state.PendingPush.RentalBillingProfiles.Add(profile);
        }, ct);

    public async Task<MobileSyncState> QueueRentalAssetDraftAsync(RentalAssetDto asset, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalAssets.RemoveAll(x => x.Id == asset.Id);
            state.PendingPush.RentalAssets.Add(asset);
        }, ct);

    public async Task<MobileSyncState> QueueRentalBillingLogDraftAsync(RentalBillingLogDto log, CancellationToken ct = default)
        => await MutateStoredStateAsync(state =>
        {
            state.PendingPush.RentalBillingLogs.RemoveAll(x => x.Id == log.Id);
            state.PendingPush.RentalBillingLogs.Add(log);
        }, ct);

    public async Task<MobileSyncState> QueuePaymentAttachmentsAsync(
        Guid paymentId,
        IEnumerable<PendingPaymentAttachmentRecord> attachments,
        CancellationToken ct = default)
    {
        var attachmentList = attachments.ToList();
        return await MutateStoredStateAsync(state =>
        {
            foreach (var attachment in attachmentList)
            {
                attachment.PaymentId = paymentId;
                state.PendingPaymentAttachments.RemoveAll(x => x.LocalId == attachment.LocalId);
                state.PendingPaymentAttachments.Add(attachment);
            }
        }, ct);
    }

    private async Task<MobileSyncState> MutateStoredStateAsync(Action<MobileSyncState> mutate, CancellationToken ct)
    {
        await _syncLock.WaitAsync(ct);
        try
        {
            var state = await _store.LoadAsync(ct);
            state.Normalize();
            mutate(state);
            await _store.SaveAsync(state, ct);
            return state;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<MobileSyncState> PullInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;

        try
        {
            var response = await _api.PullAsync(state.LastRevision, ct);
            if (response is not null)
            {
                ApplyPullResponse(state, response);
            }
        }
        catch (Exception ex)
        {
            MarkFailure(state, ex);
        }

        return state;
    }

    private async Task<MobileSyncState> PushInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;

        try
        {
            state.Normalize();
            if (HasPendingServerSyncPayload(state))
            {
                var result = await _api.PushAsync(state.PendingPush, ct);
                if (result is not null)
                {
                    state.LastRevision = Math.Max(state.LastRevision, result.CurrentServerRevision);
                    if (result.ConflictCount > 0)
                    {
                        state.PendingPush = new SyncPushRequest { DeviceId = state.DeviceId };
                        await MarkPushConflictAndRefreshAsync(state, result, ct);
                        await _store.SaveAsync(state, ct);
                        return state;
                    }
                }

                state.PendingPush = new SyncPushRequest { DeviceId = state.DeviceId };
            }

            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
            state.ConsecutiveFailureCount = 0;
            await _store.SaveAsync(state, ct);
            return state;
        }
        catch (Exception ex)
        {
            MarkFailure(state, ex);
            await _store.SaveAsync(state, ct);
            return state;
        }
    }

    private async Task<MobileSyncState> UploadPendingPaymentAttachmentsInternalAsync(MobileSyncState state, CancellationToken ct)
    {
        state.LastAttemptUtc = DateTime.UtcNow;
        var pending = state.PendingPaymentAttachments.ToList();
        if (pending.Count == 0)
        {
            state.LastError = string.Empty;
            return state;
        }

        var uploadedIds = new List<Guid>();
        var errors = new List<string>();

        foreach (var attachment in pending)
        {
            try
            {
                if (attachment.PaymentId == Guid.Empty)
                {
                    uploadedIds.Add(attachment.LocalId);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(attachment.StoredPath) || !File.Exists(attachment.StoredPath))
                {
                    uploadedIds.Add(attachment.LocalId);
                    errors.Add($"첨부 파일을 찾을 수 없어 정리했습니다: {attachment.FileName}");
                    continue;
                }

                await _api.UploadPaymentAttachmentAsync(attachment.PaymentId, attachment, ct);
                uploadedIds.Add(attachment.LocalId);
                await _attachmentStore.RemoveAsync(attachment, ct);
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        state.PendingPaymentAttachments.RemoveAll(x => uploadedIds.Contains(x.LocalId));
        if (errors.Count == 0)
        {
            state.LastSuccessUtc = DateTime.UtcNow;
            state.LastError = string.Empty;
            state.ConsecutiveFailureCount = 0;
        }
        else
        {
            state.LastError = errors[0];
            state.ConsecutiveFailureCount++;
        }

        return state;
    }

    private async Task MarkConcurrencyConflictAndRefreshAsync(MobileSyncState state, Exception ex, CancellationToken ct)
    {
        var message = BuildConcurrencyConflictMessage(ex);
        await RefreshLatestAfterConflictAsync(state, message, ct);
    }

    private async Task MarkPushConflictAndRefreshAsync(MobileSyncState state, SyncPushResult result, CancellationToken ct)
    {
        var message = BuildPushConflictMessage(result);
        await RefreshLatestAfterConflictAsync(state, message, ct);
    }

    private async Task RefreshLatestAfterConflictAsync(MobileSyncState state, string message, CancellationToken ct)
    {
        var refreshError = string.Empty;
        try
        {
            var response = await _api.PullAsync(0, ct);
            if (response is not null)
                ApplyPullResponse(state, response);
        }
        catch (Exception refreshEx)
        {
            refreshError = TranslateFailureMessage(refreshEx);
        }

        state.LastError = string.IsNullOrWhiteSpace(refreshError)
            ? message
            : $"{message} 최신 데이터 새로고침은 실패했습니다: {refreshError}";
        state.ConsecutiveFailureCount = 0;
        MobileAppLogger.Warn("SYNC", $"모바일 동시 수정 충돌: {state.LastError}");
    }

    private static string BuildConcurrencyConflictMessage(Exception ex)
    {
        var detail = ex.Message?.Trim();
        if (string.IsNullOrWhiteSpace(detail))
            return ConcurrencyConflictUserMessage;

        return detail.Length > 160
            ? $"{ConcurrencyConflictUserMessage} ({detail[..160]}...)"
            : $"{ConcurrencyConflictUserMessage} ({detail})";
    }

    private static string BuildPushConflictMessage(SyncPushResult result)
    {
        var firstConflict = result.Conflicts.FirstOrDefault();
        var reason = firstConflict?.Reason?.Trim();
        if (string.IsNullOrWhiteSpace(reason))
            return $"{ConcurrencyConflictUserMessage} (충돌 {result.ConflictCount:N0}건)";

        return reason.Length > 160
            ? $"{ConcurrencyConflictUserMessage} ({reason[..160]}...)"
            : $"{ConcurrencyConflictUserMessage} ({reason})";
    }

    private static bool IsConcurrencyConflict(Exception ex)
        => ex is HttpRequestException { StatusCode: HttpStatusCode.Conflict };

    private static bool IsNonRetryableClientFailure(Exception ex)
        => ex is HttpRequestException
        {
            StatusCode: HttpStatusCode.BadRequest
                or HttpStatusCode.Forbidden
                or HttpStatusCode.NotFound
                or HttpStatusCode.UnprocessableEntity
        };

    private static void MarkFailure(MobileSyncState state, Exception ex)
    {
        state.LastError = TranslateFailureMessage(ex);
        state.ConsecutiveFailureCount++;
        MobileAppLogger.Warn("SYNC", $"모바일 동기화 실패: {state.LastError}");
    }

    private static string TranslateFailureMessage(Exception ex)
    {
        return ex switch
        {
            MobileAuthenticationException => "인증이 만료되었거나 복구되지 않았습니다. 다시 로그인해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Unauthorized
                => "인증이 만료되었거나 복구되지 않았습니다. 다시 로그인해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.InternalServerError
                => "서버 오류(500)가 발생했습니다. 잠시 후 다시 시도해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Conflict
                => ConcurrencyConflictUserMessage,
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.BadRequest
                => BuildClientValidationMessage(httpEx),
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.Forbidden
                => "현재 계정 권한 또는 지점 범위 때문에 저장할 수 없습니다. 담당지점/권한을 확인해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode == HttpStatusCode.NotFound
                => "저장 대상 데이터를 찾지 못했습니다. 최신 데이터를 다시 불러온 뒤 시도해 주세요.",
            HttpRequestException httpEx when httpEx.StatusCode.HasValue
                => $"서버 요청에 실패했습니다. ({(int)httpEx.StatusCode.Value} {httpEx.StatusCode.Value})",
            TaskCanceledException or TimeoutException
                => "네트워크 응답이 지연되고 있습니다. 잠시 후 다시 시도해 주세요.",
            HttpRequestException
                => "네트워크 연결을 확인한 후 다시 시도해 주세요.",
            _ => string.IsNullOrWhiteSpace(ex.Message)
                ? "알 수 없는 오류가 발생했습니다. 잠시 후 다시 시도해 주세요."
                : ex.Message
        };
    }

    private static string BuildClientValidationMessage(HttpRequestException httpEx)
    {
        var detail = ExtractHttpErrorDetail(httpEx);
        return string.IsNullOrWhiteSpace(detail)
            ? "저장할 수 없습니다. 입력값, 거래처, 품목, 재고 상태를 확인해 주세요."
            : $"저장할 수 없습니다. {detail}";
    }

    private static string ExtractHttpErrorDetail(HttpRequestException httpEx)
    {
        var message = httpEx.Message?.Trim() ?? string.Empty;
        if (message.Length == 0)
            return string.Empty;

        var body = message;
        if (body.StartsWith("400", StringComparison.Ordinal))
        {
            body = body[3..].TrimStart();
            if (body.StartsWith("Bad Request", StringComparison.OrdinalIgnoreCase))
                body = body["Bad Request".Length..].TrimStart();
            else if (body.StartsWith("BadRequest", StringComparison.OrdinalIgnoreCase))
                body = body["BadRequest".Length..].TrimStart();
        }

        if (body.Length == 0)
            return string.Empty;

        if (body[0] == '{')
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.TryGetProperty("message", out var messageProperty) &&
                    messageProperty.ValueKind == JsonValueKind.String)
                {
                    var parsed = messageProperty.GetString();
                    if (!string.IsNullOrWhiteSpace(parsed))
                        return TrimForStatus(parsed);
                }
            }
            catch (JsonException)
            {
                // Fall through to a plain-text fallback.
            }
        }

        if (body.StartsWith("<", StringComparison.Ordinal))
            return string.Empty;

        return TrimForStatus(body);
    }

    private static string TrimForStatus(string value)
    {
        var normalized = string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized.Length <= 160 ? normalized : normalized[..160] + "...";
    }

    private static bool HasPendingServerSyncPayload(MobileSyncState state)
    {
        state.Normalize();
        return (state.PendingPush.CompanyProfiles?.Count ?? 0) > 0
            || (state.PendingPush.Units?.Count ?? 0) > 0
            || (state.PendingPush.CustomerCategories?.Count ?? 0) > 0
            || (state.PendingPush.PriceGradeOptions?.Count ?? 0) > 0
            || (state.PendingPush.TradeTypeOptions?.Count ?? 0) > 0
            || (state.PendingPush.ItemCategoryOptions?.Count ?? 0) > 0
            || (state.PendingPush.CustomerMasters?.Count ?? 0) > 0
            || (state.PendingPush.Customers?.Count ?? 0) > 0
            || (state.PendingPush.CustomerContracts?.Count ?? 0) > 0
            || (state.PendingPush.Items?.Count ?? 0) > 0
            || (state.PendingPush.ItemWarehouseStocks?.Count ?? 0) > 0
            || (state.PendingPush.Transactions?.Count ?? 0) > 0
            || (state.PendingPush.TransactionAttachments?.Count ?? 0) > 0
            || (state.PendingPush.InventoryTransfers?.Count ?? 0) > 0
            || (state.PendingPush.RentalManagementCompanies?.Count ?? 0) > 0
            || (state.PendingPush.RentalBillingProfiles?.Count ?? 0) > 0
            || (state.PendingPush.RentalAssets?.Count ?? 0) > 0
            || (state.PendingPush.RentalBillingLogs?.Count ?? 0) > 0
            || (state.PendingPush.Invoices?.Count ?? 0) > 0
            || (state.PendingPush.Payments?.Count ?? 0) > 0;
    }

    private static void ApplyPullResponse(MobileSyncState state, SyncPullResponse response)
    {
        state.Normalize();
        state.LastRevision = Math.Max(state.LastRevision, response.CurrentServerRevision);
        state.LastSuccessUtc = DateTime.UtcNow;
        state.LastError = string.Empty;
        state.ConsecutiveFailureCount = 0;
        state.LastPulledCustomerCount = response.Customers.Count;
        state.LastPulledItemCount = response.Items.Count;
        state.LastPulledPriceGradeOptionCount = response.PriceGradeOptions.Count;
        state.LastPulledInvoiceCount = response.Invoices.Count;
        state.LastPulledPaymentCount = response.Payments.Count;
        state.LastPulledTransactionCount = response.Transactions.Count;
        state.LastPulledTransactionAttachmentCount = response.TransactionAttachments.Count;
        state.LastPulledInventoryTransferCount = response.InventoryTransfers.Count;
        state.LastPulledRentalManagementCompanyCount = response.RentalManagementCompanies.Count;
        state.LastPulledRentalBillingProfileCount = response.RentalBillingProfiles.Count;
        state.LastPulledRentalAssetCount = response.RentalAssets.Count;
        state.LastPulledRentalBillingLogCount = response.RentalBillingLogs.Count;
        state.SyncedPriceGradeOptions = MergeById(state.SyncedPriceGradeOptions, response.PriceGradeOptions);
        state.SyncedInvoices = MergeById(state.SyncedInvoices, response.Invoices);
        state.SyncedPayments = MergeById(state.SyncedPayments, response.Payments);
        state.SyncedTransactions = MergeById(state.SyncedTransactions, response.Transactions);
        state.SyncedTransactionAttachments = MergeById(state.SyncedTransactionAttachments, response.TransactionAttachments);
        state.SyncedInventoryTransfers = MergeById(state.SyncedInventoryTransfers, response.InventoryTransfers);
        state.SyncedRentalManagementCompanies = MergeById(state.SyncedRentalManagementCompanies, response.RentalManagementCompanies);
        state.SyncedRentalBillingProfiles = MergeById(state.SyncedRentalBillingProfiles, response.RentalBillingProfiles);
        state.SyncedRentalAssets = MergeById(state.SyncedRentalAssets, response.RentalAssets);
        state.SyncedRentalBillingLogs = MergeById(state.SyncedRentalBillingLogs, response.RentalBillingLogs);
    }

    private static List<T> MergeById<T>(IEnumerable<T>? existing, IEnumerable<T>? incoming) where T : SyncEntityDto
    {
        var map = (existing ?? Enumerable.Empty<T>())
            .Where(item => item.Id != Guid.Empty && !item.IsDeleted)
            .ToDictionary(item => item.Id, item => item);

        foreach (var item in incoming ?? Enumerable.Empty<T>())
        {
            if (item.Id == Guid.Empty)
                continue;

            if (item.IsDeleted)
                map.Remove(item.Id);
            else
                map[item.Id] = item;
        }

        return map.Values.ToList();
    }
}
