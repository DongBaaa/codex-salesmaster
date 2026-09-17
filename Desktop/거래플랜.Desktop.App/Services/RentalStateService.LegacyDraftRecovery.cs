using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public enum RentalDraftKind { Billing, Onboarding }

public sealed record RentalLegacyDraftPreview(
    RentalDraftKind Kind, string CustomerName, string OfficeHint, string TargetBusinessLabel,
    bool TargetHasDraft, string PayloadHash, string LegacyKey, string TargetKey,
    Guid SessionId, long ScopeEpoch);

public sealed partial class RentalStateService
{
    private readonly SemaphoreSlim _legacyDraftDataGate = new(1, 1);

    public async Task<RentalLegacyDraftPreview?> GetLegacyDraftPreviewAsync(
        RentalDraftKind kind, SessionState session, CancellationToken ct = default)
    {
        var gate = _local?.OwnerScopeDataGate ?? _legacyDraftDataGate;
        await gate.WaitAsync(ct);
        try
        {
            using var scope = await session.AcquireSyncScopeCommitLeaseAsync(ct);
            if (!session.IsLoggedIn) return null;
            var legacyKey = BuildLegacyDraftKey(kind, session);
            var payload = await ReadLegacyDraftSettingAsync(legacyKey, ct);
            if (string.IsNullOrWhiteSpace(payload)) return null;
            var hash = HashLegacyDraft(payload);
            if (await ReadLegacyDraftSettingAsync(LegacyRecoveryMarkerKey(legacyKey, hash), ct) is not null)
                return null;
            var (customer, office) = ReadLegacyDraftSummary(kind, payload);
            var targetKey = BuildDraftSettingKey(DraftPrefix(kind), session);
            return new(kind, customer, office, session.SelectedBusinessDatabaseLabel,
                await ReadLegacyDraftSettingAsync(targetKey, ct) is not null,
                hash, legacyKey, targetKey, session.SessionId, session.SyncScopeEpoch);
        }
        finally { gate.Release(); }
    }

    public async Task RecoverLegacyDraftAsync(
        RentalLegacyDraftPreview preview, SessionState session, CancellationToken ct = default)
    {
        var gate = _local?.OwnerScopeDataGate ?? _legacyDraftDataGate;
        await gate.WaitAsync(ct);
        try
        {
            using var scope = await session.AcquireSyncScopeCommitLeaseAsync(ct);
            if (!session.IsLoggedIn || session.SessionId != preview.SessionId || session.SyncScopeEpoch != preview.ScopeEpoch ||
                BuildLegacyDraftKey(preview.Kind, session) != preview.LegacyKey ||
                BuildDraftSettingKey(DraftPrefix(preview.Kind), session) != preview.TargetKey)
                throw new InvalidOperationException("확인 중 계정 또는 업체가 변경되었습니다. 임시본을 다시 확인해 주세요.");
            if (_db.ChangeTracker.HasChanges())
                throw new InvalidOperationException("진행 중인 저장이 있습니다. 저장이 끝난 뒤 임시본을 다시 확인해 주세요.");

            await using var transaction = await _db.BeginRuntimeMutationTransactionAsync(ct);
            var payload = await ReadLegacyDraftSettingAsync(preview.LegacyKey, ct);
            if (payload is null || HashLegacyDraft(payload) != preview.PayloadHash)
                throw new InvalidOperationException("확인 중 이전 임시본이 변경되었습니다. 다시 확인해 주세요.");
            ReadLegacyDraftSummary(preview.Kind, payload);
            var markerKey = LegacyRecoveryMarkerKey(preview.LegacyKey, preview.PayloadHash);
            if (await ReadLegacyDraftSettingAsync(markerKey, ct) is not null)
                throw new InvalidOperationException("이 임시본은 이미 복원되었습니다. 원문은 보관되어 있습니다.");
            if (await ReadLegacyDraftSettingAsync(preview.TargetKey, ct) is not null)
                throw new InvalidOperationException("현재 업체에 작성 중인 임시본이 있습니다. 먼저 저장하거나 내용을 정리해 주세요.");

            var draft = new LocalSetting { Key = preview.TargetKey, Value = payload };
            var marker = new LocalSetting { Key = markerKey, Value = preview.TargetKey };
            try
            {
                _db.Settings.AddRange(draft, marker);
                await _db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                _db.Entry(draft).State = EntityState.Detached;
                _db.Entry(marker).State = EntityState.Detached;
                throw;
            }
        }
        finally { gate.Release(); }
    }

    private Task<string?> ReadLegacyDraftSettingAsync(string key, CancellationToken ct)
        => _db.Settings.AsNoTracking().Where(s => s.Key == key).Select(s => s.Value).FirstOrDefaultAsync(ct);

    private static string DraftPrefix(RentalDraftKind kind) => kind switch
    {
        RentalDraftKind.Billing => BillingEditorDraftSettingPrefix,
        RentalDraftKind.Onboarding => OnboardingDraftSettingPrefix,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static string BuildLegacyDraftKey(RentalDraftKind kind, SessionState session)
    {
        var office = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(session.OfficeCode, DomainConstants.OfficeUsenet);
        var username = (session.User?.Username ?? "anonymous").Trim();
        if (username.Length == 0) username = "anonymous";
        return $"{DraftPrefix(kind)}.{office}.{username}".ToUpperInvariant();
    }

    private static string HashLegacyDraft(string payload) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    private static string LegacyRecoveryMarkerKey(string key, string hash) => $"{key}.RECOVERED.{hash}";

    private static (string Customer, string Office) ReadLegacyDraftSummary(RentalDraftKind kind, string payload)
    {
        try
        {
            if (kind == RentalDraftKind.Billing)
            {
                var draft = JsonSerializer.Deserialize<RentalBillingEditorDraftModel>(payload, RentalJsonOptions)
                    ?? throw new JsonException();
                return (draft.CustomerName, draft.OfficeCode);
            }
            var onboarding = JsonSerializer.Deserialize<RentalCustomerOnboardingDraftModel>(payload, RentalJsonOptions)
                ?? throw new JsonException();
            return (onboarding.CustomerName, onboarding.OfficeCode);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("이전 임시본을 읽을 수 없습니다. 원문은 보관되어 있으니 데이터 점검을 요청해 주세요.");
        }
    }
}
