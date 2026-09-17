using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    // A stored office credential also creates a fresh authenticated session. Recover
    // its receipts without replacing the interactive user's last-login scope settings.
    internal async Task ResumeOutboxAfterOnlineLoginAsync(SessionState session, CancellationToken ct)
    {
        Guid loginSessionId;
        long loginScopeEpoch;
        using (session.AcquireSyncScopeSnapshotLease())
        {
            loginSessionId = session.SessionId;
            loginScopeEpoch = session.SyncScopeEpoch;
        }

        await using var db = CreateIndependentAuthenticationDb();
        await using var transaction = await db.BeginRuntimeMutationTransactionAsync(ct);
        using var loginLease = await session.AcquireSyncScopeCommitLeaseAsync(ct);
        if (session.SessionId != loginSessionId || session.SyncScopeEpoch != loginScopeEpoch)
            throw new InvalidOperationException("로그인 계정 범위가 변경되어 미전송 기록 인계를 중단했습니다.");

        await ResumeLoginOutboxOwnershipAsync(db, session, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    // Called only during successful online login, inside the login-settings transaction
    // and its session lease. Push/ack still require an exact current-session receipt.
    private static async Task ResumeLoginOutboxOwnershipAsync(
        LocalDbContext db,
        SessionState session,
        CancellationToken ct)
    {
        if (!session.IsLoggedIn || session.IsOfflineMode || session.IsTokenExpired ||
            string.IsNullOrWhiteSpace(session.Token) || session.User!.UserId == Guid.Empty)
            return;

        var deviceId = (await db.Settings.AsNoTracking()
            .Where(setting => setting.Key == "Sync.DeviceId")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync(ct))?.Trim();
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        var rows = await db.SyncOutboxEntries
            .Where(row => row.UserId == session.User.UserId &&
                row.SessionId != Guid.Empty && row.SessionId != session.SessionId &&
                (row.Status == "Prepared" || row.Status == "Sent" || row.Status == "Failed"))
            .ToListAsync(ct);
        foreach (var row in rows)
        {
            if (row.Id == Guid.Empty || row.EntityId == Guid.Empty ||
                string.IsNullOrWhiteSpace(row.EntityName) || string.IsNullOrWhiteSpace(row.MutationId) ||
                !string.Equals(row.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase) ||
                !TenantScopeCatalog.TryNormalizeTenantCode(row.TenantCode, out var tenant) ||
                string.IsNullOrWhiteSpace(row.BusinessDatabaseName) ||
                !string.Equals(TenantScopeCatalog.GetDatabaseName(tenant),
                    TenantScopeCatalog.GetDatabaseName(row.BusinessDatabaseName), StringComparison.OrdinalIgnoreCase) ||
                !OfficeCodeCatalog.TryNormalizeScope(row.OfficeCode, out var office) ||
                !OfficeCodeCatalog.TryNormalizeOfficeCode(row.ResponsibleOfficeCode, out var responsible) ||
                (office != OfficeCodeCatalog.Shared && !TenantScopeCatalog.TenantContainsOffice(tenant, office)) ||
                !TenantScopeCatalog.TenantContainsOffice(tenant, responsible) ||
                (!session.HasGlobalDataScope && !string.Equals(tenant, session.AuthenticatedTenantCode, StringComparison.OrdinalIgnoreCase)) ||
                !CanWriteOfficeScope(session, responsible, office))
                continue;

            // Retain the mutation ID and all send/acceptance evidence: the server may
            // already have committed a request whose response was lost before exit.
            row.SessionId = session.SessionId;
        }
    }
}
