using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record SyncDiagnosticSnapshot(
    long LastKnownSyncRevision,
    string LastKnownSyncError,
    int DirtyCustomerMasterCount,
    int DirtyCustomerCount,
    int DirtyInvoiceCount,
    int DirtyTransactionCount,
    int DirtyAttachmentCount,
    int DirtyPaymentCount,
    int DirtyRentalAssetCount,
    int DirtyInventoryTransferCount,
    int MissingCustomerReferenceCount,
    int MissingInvoiceReferenceCount,
    int MissingTransactionReferenceCount,
    int MissingRentalItemReferenceCount);

public sealed record SyncDiagnosticSummary(
    int OpenIssueCount,
    int RecoverableIssueCount,
    int TotalIssueCount,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    string LastError,
    long LastKnownSyncRevision);

public sealed record SyncDiagnosticFilter(
    string SearchText,
    string Category,
    string Status,
    string Severity,
    bool OnlyRecoverable);

public sealed class SyncDiagnosticListItem
{
    public Guid Id { get; init; }
    public DateTime OccurredAtUtc { get; init; }
    public DateTime LastOccurredAtUtc { get; init; }
    public int OccurrenceCount { get; init; }
    public string Severity { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Subcategory { get; init; } = string.Empty;
    public string EntityName { get; init; } = string.Empty;
    public string EntityId { get; init; } = string.Empty;
    public string ReferenceEntityName { get; init; } = string.Empty;
    public string ReferenceEntityId { get; init; } = string.Empty;
    public string UserName { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public string TenantCode { get; init; } = string.Empty;
    public string MachineName { get; init; } = string.Empty;
    public string AppVersion { get; init; } = string.Empty;
    public string SyncPhase { get; init; } = string.Empty;
    public string RawMessage { get; init; } = string.Empty;
    public string NormalizedMessage { get; init; } = string.Empty;
    public string StackTrace { get; init; } = string.Empty;
    public bool IsRecoverable { get; init; }
    public string RecoveryAction { get; init; } = string.Empty;
    public bool RecoveryAttempted { get; init; }
    public bool RecoverySucceeded { get; init; }
    public DateTime? ResolvedAtUtc { get; init; }
    public string Status { get; init; } = string.Empty;
    public SyncDiagnosticSnapshot Snapshot { get; init; } = new(0, string.Empty, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);
    public string SummaryText => string.IsNullOrWhiteSpace(EntityName)
        ? Category
        : string.IsNullOrWhiteSpace(EntityId)
            ? $"{EntityName}"
            : $"{EntityName} {EntityId}";
    public string UserScopeText
    {
        get
        {
            var values = new[] { UserName, OfficeCode, TenantCode }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return values.Length == 0 ? "-" : string.Join(" / ", values);
        }
    }
    public string MachineVersionText
    {
        get
        {
            var values = new[] { MachineName, AppVersion }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray();
            return values.Length == 0 ? "-" : string.Join(" / ", values);
        }
    }
    public string ReferenceText => string.IsNullOrWhiteSpace(ReferenceEntityName)
        ? "-"
        : string.IsNullOrWhiteSpace(ReferenceEntityId)
            ? ReferenceEntityName
            : $"{ReferenceEntityName} {ReferenceEntityId}";
}

public sealed class SyncDiagnosticsService
{
    private static readonly Regex EntityConflictPattern = new(
        @"(?<entity>[A-Za-z][A-Za-z0-9]+)\s+(?<entityId>[0-9a-fA-F\-]{36})\s+-\s+(?<reason>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MissingReferencePattern = new(
        @"Referenced\s+(?<refEntity>[A-Za-z][A-Za-z0-9]+)\s+was\s+not\s+found:\s+(?<refId>[0-9a-fA-F\-]{36})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{4}\-[0-9a-fA-F]{12}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DeferredDirtyMissingCredentialPattern = new(
        @"저장된\s+지점\s+동기화\s+계정\s+없음으로\s+dirty\s+보류:\s*scope=(?<scope>[^,]+),\s*office=(?<office>[^,]*),\s*tenant=(?<tenant>[^,]*),\s*count=(?<count>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex RemainingDirtyScopePattern = new(
        @"동기화\s+후\s+dirty\s+잔존:\s*scope=(?<scope>[^,]+),\s*office=(?<office>[^,]*),\s*tenant=(?<tenant>[^,]*),\s*count=(?<count>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private readonly SessionState _session;

    public event Action? DiagnosticsChanged;

    public SyncDiagnosticsService(SessionState session)
    {
        _session = session;
    }

    public async Task RecordIssueAsync(
        string phase,
        string rawMessage,
        Exception? exception = null,
        string? severity = null,
        bool recoveryAttempted = false,
        bool recoverySucceeded = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawMessage) && exception is null)
            return;

        var detail = string.IsNullOrWhiteSpace(rawMessage)
            ? exception?.Message ?? string.Empty
            : rawMessage.Trim();
        var classification = Classify(phase, detail, exception, severity, recoveryAttempted, recoverySucceeded);
        await using var db = CreateDbContext();
        var snapshot = await CaptureSnapshotAsync(db, ct);
        var nowUtc = DateTime.UtcNow;

        var existing = await db.SyncDiagnosticEvents
            .Where(current => current.Status == "Open"
                              && current.SyncPhase == classification.SyncPhase
                              && current.NormalizedMessage == classification.NormalizedMessage
                              && current.EntityName == classification.EntityName
                              && current.EntityId == classification.EntityId
                              && current.ReferenceEntityName == classification.ReferenceEntityName
                              && current.ReferenceEntityId == classification.ReferenceEntityId)
            .OrderByDescending(current => current.LastOccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        if (existing is not null && nowUtc - existing.LastOccurredAtUtc <= TimeSpan.FromHours(12))
        {
            existing.LastOccurredAtUtc = nowUtc;
            existing.OccurrenceCount += 1;
            existing.RawMessage = detail;
            existing.StackTrace = exception?.ToString() ?? existing.StackTrace;
            existing.LastKnownSyncRevision = snapshot.LastKnownSyncRevision;
            existing.LastKnownSyncError = snapshot.LastKnownSyncError;
            ApplySnapshot(existing, snapshot);
        }
        else
        {
            var entity = new LocalSyncDiagnosticEvent
            {
                Id = Guid.NewGuid(),
                OccurredAtUtc = nowUtc,
                LastOccurredAtUtc = nowUtc,
                OccurrenceCount = 1,
                Severity = classification.Severity,
                Category = classification.Category,
                Subcategory = classification.Subcategory,
                EntityName = classification.EntityName,
                EntityId = classification.EntityId,
                ReferenceEntityName = classification.ReferenceEntityName,
                ReferenceEntityId = classification.ReferenceEntityId,
                UserName = _session.User?.Username ?? string.Empty,
                OfficeCode = _session.OfficeCode,
                TenantCode = _session.TenantCode,
                MachineName = Environment.MachineName,
                AppVersion = GetAppVersion(),
                SyncPhase = classification.SyncPhase,
                RawMessage = detail,
                NormalizedMessage = classification.NormalizedMessage,
                StackTrace = exception?.ToString() ?? string.Empty,
                IsRecoverable = classification.IsRecoverable,
                RecoveryAction = classification.RecoveryAction,
                RecoveryAttempted = recoveryAttempted,
                RecoverySucceeded = recoverySucceeded,
                Status = recoverySucceeded ? "Recovered" : "Open",
                ResolvedAtUtc = recoverySucceeded ? nowUtc : null,
                LastKnownSyncRevision = snapshot.LastKnownSyncRevision,
                LastKnownSyncError = snapshot.LastKnownSyncError
            };

            ApplySnapshot(entity, snapshot);
            db.SyncDiagnosticEvents.Add(entity);
        }

        await db.SaveChangesAsync(ct);
        DiagnosticsChanged?.Invoke();
    }

    public async Task ResolveOpenIssuesAsync(string? phase = null, CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        var query = db.SyncDiagnosticEvents.Where(current => current.Status == "Open");
        if (!string.IsNullOrWhiteSpace(phase))
            query = query.Where(current => current.SyncPhase == phase);

        var events = await query.ToListAsync(ct);
        if (events.Count == 0)
            return;

        var nowUtc = DateTime.UtcNow;
        foreach (var current in events)
        {
            current.Status = "Resolved";
            current.ResolvedAtUtc = nowUtc;
            current.RecoveryAttempted = true;
            current.RecoverySucceeded = true;
        }

        await db.SaveChangesAsync(ct);
        DiagnosticsChanged?.Invoke();
    }

    public async Task<SyncDiagnosticSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        var openIssueCount = await db.SyncDiagnosticEvents.CountAsync(current => current.Status == "Open", ct);
        var recoverableIssueCount = await db.SyncDiagnosticEvents.CountAsync(current => current.Status == "Open" && current.IsRecoverable, ct);
        var totalIssueCount = await db.SyncDiagnosticEvents.CountAsync(ct);
        var lastFailure = await db.SyncDiagnosticEvents
            .Where(current => current.Status == "Open")
            .OrderByDescending(current => current.LastOccurredAtUtc)
            .Select(current => (DateTime?)current.LastOccurredAtUtc)
            .FirstOrDefaultAsync(ct);

        var lastSuccessRaw = await GetSettingAsync(db, "Sync.LastSuccessAt", ct);
        var lastError = await GetSettingAsync(db, "Sync.LastError", ct) ?? string.Empty;
        var lastSyncRevisionRaw = await GetSettingAsync(db, "LastSyncRevision", ct);
        _ = long.TryParse(lastSyncRevisionRaw, out var lastKnownSyncRevision);
        DateTime? lastSuccessAtUtc = null;
        if (DateTime.TryParse(lastSuccessRaw, out var parsedLastSuccess))
            lastSuccessAtUtc = parsedLastSuccess.ToUniversalTime();

        return new SyncDiagnosticSummary(
            openIssueCount,
            recoverableIssueCount,
            totalIssueCount,
            lastSuccessAtUtc,
            lastFailure,
            lastError,
            lastKnownSyncRevision);
    }

    public async Task<IReadOnlyList<SyncDiagnosticListItem>> GetEventsAsync(SyncDiagnosticFilter filter, CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        var query = db.SyncDiagnosticEvents.AsQueryable();

        if (!string.IsNullOrWhiteSpace(filter.Category) && !string.Equals(filter.Category, "전체", StringComparison.Ordinal))
            query = query.Where(current => current.Category == filter.Category);

        if (!string.IsNullOrWhiteSpace(filter.Status) && !string.Equals(filter.Status, "전체", StringComparison.Ordinal))
            query = query.Where(current => current.Status == filter.Status);

        if (!string.IsNullOrWhiteSpace(filter.Severity) && !string.Equals(filter.Severity, "전체", StringComparison.Ordinal))
            query = query.Where(current => current.Severity == filter.Severity);

        if (filter.OnlyRecoverable)
            query = query.Where(current => current.IsRecoverable);

        var searchText = filter.SearchText?.Trim() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            query = query.Where(current =>
                current.RawMessage.Contains(searchText) ||
                current.EntityName.Contains(searchText) ||
                current.EntityId.Contains(searchText) ||
                current.ReferenceEntityName.Contains(searchText) ||
                current.ReferenceEntityId.Contains(searchText) ||
                current.UserName.Contains(searchText) ||
                current.OfficeCode.Contains(searchText));
        }

        var rows = await query
            .OrderByDescending(current => current.LastOccurredAtUtc)
            .Take(300)
            .ToListAsync(ct);

        return rows.Select(ToListItem).ToList();
    }

    public async Task<string> ExportReportAsync(IReadOnlyCollection<Guid>? eventIds = null, CancellationToken ct = default)
    {
        var summary = await GetSummaryAsync(ct);
        await using var db = CreateDbContext();
        var eventsQuery = db.SyncDiagnosticEvents.AsQueryable();
        if (eventIds is { Count: > 0 })
            eventsQuery = eventsQuery.Where(current => eventIds.Contains(current.Id));

        var events = await eventsQuery
            .OrderByDescending(current => current.LastOccurredAtUtc)
            .Take(300)
            .ToListAsync(ct);

        var report = new
        {
            GeneratedAtUtc = DateTime.UtcNow,
            MachineName = Environment.MachineName,
            AppVersion = GetAppVersion(),
            User = _session.User?.Username ?? string.Empty,
            OfficeCode = _session.OfficeCode,
            TenantCode = _session.TenantCode,
            Summary = summary,
            Events = events.Select(ToListItem).ToList(),
            LatestLogTail = ReadRecentLogTail()
        };

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var jsonPath = Path.Combine(AppPaths.DiagnosticsDir, $"sync-diagnostics-{stamp}.json");
        var markdownPath = Path.Combine(AppPaths.DiagnosticsDir, $"sync-diagnostics-{stamp}.md");
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(jsonPath, json, Encoding.UTF8, ct);
        await File.WriteAllTextAsync(markdownPath, BuildMarkdownReport(summary, events.Select(ToListItem).ToList()), Encoding.UTF8, ct);
        return jsonPath;
    }

    public void OpenLogFolder()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = AppPaths.LogDir,
            UseShellExecute = true
        });
    }

    public async Task ClearResolvedEventsAsync(CancellationToken ct = default)
    {
        await using var db = CreateDbContext();
        await db.SyncDiagnosticEvents
            .Where(current => current.Status != "Open")
            .ExecuteDeleteAsync(ct);
        DiagnosticsChanged?.Invoke();
    }

    private static async Task<SyncDiagnosticSnapshot> CaptureSnapshotAsync(LocalDbContext db, CancellationToken ct)
    {
        var lastSyncRevisionRaw = await GetSettingAsync(db, "LastSyncRevision", ct);
        var lastError = await GetSettingAsync(db, "Sync.LastError", ct) ?? string.Empty;
        _ = long.TryParse(lastSyncRevisionRaw, out var lastKnownSyncRevision);

        var dirtyCustomerMasterCount = await db.CustomerMasters.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyCustomerCount = await db.Customers.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyInvoiceCount = await db.Invoices.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyTransactionCount = await db.Transactions.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyAttachmentCount = await db.TransactionAttachments.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyPaymentCount = await db.Payments.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyRentalAssetCount = await db.RentalAssets.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);
        var dirtyInventoryTransferCount = await db.InventoryTransfers.IgnoreQueryFilters().CountAsync(current => current.IsDirty, ct);

        var customerIds = await db.Customers.IgnoreQueryFilters().Select(current => current.Id).ToListAsync(ct);
        var invoiceIds = await db.Invoices.IgnoreQueryFilters().Select(current => current.Id).ToListAsync(ct);
        var transactionIds = await db.Transactions.IgnoreQueryFilters().Select(current => current.Id).ToListAsync(ct);
        var itemIds = await db.Items.IgnoreQueryFilters().Select(current => current.Id).ToListAsync(ct);

        var missingCustomerReferenceCount = await db.Invoices.IgnoreQueryFilters()
            .CountAsync(current => current.CustomerId != Guid.Empty && !customerIds.Contains(current.CustomerId), ct);
        var missingInvoiceReferenceCount = await db.Payments.IgnoreQueryFilters()
            .CountAsync(current => current.InvoiceId != Guid.Empty && !invoiceIds.Contains(current.InvoiceId), ct);
        var missingTransactionReferenceCount = await db.TransactionAttachments.IgnoreQueryFilters()
            .CountAsync(current => current.TransactionId != Guid.Empty && !transactionIds.Contains(current.TransactionId), ct);
        var missingRentalItemReferenceCount = await db.RentalAssets.IgnoreQueryFilters()
            .CountAsync(current => current.ItemId.HasValue && !itemIds.Contains(current.ItemId.Value), ct);

        return new SyncDiagnosticSnapshot(
            lastKnownSyncRevision,
            lastError,
            dirtyCustomerMasterCount,
            dirtyCustomerCount,
            dirtyInvoiceCount,
            dirtyTransactionCount,
            dirtyAttachmentCount,
            dirtyPaymentCount,
            dirtyRentalAssetCount,
            dirtyInventoryTransferCount,
            missingCustomerReferenceCount,
            missingInvoiceReferenceCount,
            missingTransactionReferenceCount,
            missingRentalItemReferenceCount);
    }

    private static async Task<string?> GetSettingAsync(LocalDbContext db, string key, CancellationToken ct)
    {
        var setting = await db.Settings.FindAsync([key], ct);
        return setting?.Value;
    }

    private static LocalDbContext CreateDbContext() => new();

    private static void ApplySnapshot(LocalSyncDiagnosticEvent entity, SyncDiagnosticSnapshot snapshot)
    {
        entity.LastKnownSyncRevision = snapshot.LastKnownSyncRevision;
        entity.LastKnownSyncError = snapshot.LastKnownSyncError;
        entity.DirtyCustomerMasterCount = snapshot.DirtyCustomerMasterCount;
        entity.DirtyCustomerCount = snapshot.DirtyCustomerCount;
        entity.DirtyInvoiceCount = snapshot.DirtyInvoiceCount;
        entity.DirtyTransactionCount = snapshot.DirtyTransactionCount;
        entity.DirtyAttachmentCount = snapshot.DirtyAttachmentCount;
        entity.DirtyPaymentCount = snapshot.DirtyPaymentCount;
        entity.DirtyRentalAssetCount = snapshot.DirtyRentalAssetCount;
        entity.DirtyInventoryTransferCount = snapshot.DirtyInventoryTransferCount;
        entity.MissingCustomerReferenceCount = snapshot.MissingCustomerReferenceCount;
        entity.MissingInvoiceReferenceCount = snapshot.MissingInvoiceReferenceCount;
        entity.MissingTransactionReferenceCount = snapshot.MissingTransactionReferenceCount;
        entity.MissingRentalItemReferenceCount = snapshot.MissingRentalItemReferenceCount;
    }

    private static SyncDiagnosticListItem ToListItem(LocalSyncDiagnosticEvent entity)
        => new()
        {
            Id = entity.Id,
            OccurredAtUtc = entity.OccurredAtUtc,
            LastOccurredAtUtc = entity.LastOccurredAtUtc,
            OccurrenceCount = entity.OccurrenceCount,
            Severity = entity.Severity,
            Category = entity.Category,
            Subcategory = entity.Subcategory,
            EntityName = entity.EntityName,
            EntityId = entity.EntityId,
            ReferenceEntityName = entity.ReferenceEntityName,
            ReferenceEntityId = entity.ReferenceEntityId,
            UserName = entity.UserName,
            OfficeCode = entity.OfficeCode,
            TenantCode = entity.TenantCode,
            MachineName = entity.MachineName,
            AppVersion = entity.AppVersion,
            SyncPhase = entity.SyncPhase,
            RawMessage = entity.RawMessage,
            NormalizedMessage = entity.NormalizedMessage,
            StackTrace = entity.StackTrace,
            IsRecoverable = entity.IsRecoverable,
            RecoveryAction = entity.RecoveryAction,
            RecoveryAttempted = entity.RecoveryAttempted,
            RecoverySucceeded = entity.RecoverySucceeded,
            ResolvedAtUtc = entity.ResolvedAtUtc,
            Status = entity.Status,
            Snapshot = new SyncDiagnosticSnapshot(
                entity.LastKnownSyncRevision,
                entity.LastKnownSyncError,
                entity.DirtyCustomerMasterCount,
                entity.DirtyCustomerCount,
                entity.DirtyInvoiceCount,
                entity.DirtyTransactionCount,
                entity.DirtyAttachmentCount,
                entity.DirtyPaymentCount,
                entity.DirtyRentalAssetCount,
                entity.DirtyInventoryTransferCount,
                entity.MissingCustomerReferenceCount,
                entity.MissingInvoiceReferenceCount,
                entity.MissingTransactionReferenceCount,
                entity.MissingRentalItemReferenceCount)
        };

    private static string GetAppVersion()
        => Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
           ?? typeof(SyncDiagnosticsService).Assembly.GetName().Version?.ToString(3)
           ?? string.Empty;

    private static string ReadRecentLogTail()
    {
        try
        {
            var latestLog = Directory.Exists(AppPaths.LogDir)
                ? new DirectoryInfo(AppPaths.LogDir)
                    .GetFiles("*.log")
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault()
                : null;
            if (latestLog is null)
                return string.Empty;

            var lines = File.ReadLines(latestLog.FullName).TakeLast(120).ToArray();
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string BuildMarkdownReport(SyncDiagnosticSummary summary, IReadOnlyList<SyncDiagnosticListItem> events)
    {
        var manualReviewIssueCount = Math.Max(0, summary.OpenIssueCount - summary.RecoverableIssueCount);
        var builder = new StringBuilder();
        builder.AppendLine("# 동기화 진단 리포트");
        builder.AppendLine();
        builder.AppendLine($"- 생성시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        builder.AppendLine($"- 미해결 오류: {summary.OpenIssueCount:N0}건");
        builder.AppendLine($"- 자동 복구 가능: {summary.RecoverableIssueCount:N0}건");
        builder.AppendLine($"- 수동 확인 필요: {manualReviewIssueCount:N0}건");
        builder.AppendLine($"- 마지막 성공: {(summary.LastSuccessAtUtc.HasValue ? summary.LastSuccessAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "없음")}");
        builder.AppendLine($"- 마지막 오류: {(string.IsNullOrWhiteSpace(summary.LastError) ? "없음" : summary.LastError)}");
        builder.AppendLine();
        builder.AppendLine("## 최근 진단 이벤트");
        builder.AppendLine();
        foreach (var item in events.Take(50))
        {
            builder.AppendLine($"- [{item.Status}] {item.LastOccurredAtUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} / {item.Category} / {item.SummaryText} / {item.RawMessage}");
        }

        return builder.ToString();
    }

    private static SyncDiagnosticClassification Classify(
        string phase,
        string detail,
        Exception? exception,
        string? severity,
        bool recoveryAttempted,
        bool recoverySucceeded)
    {
        var normalized = GuidPattern.Replace(detail, "{id}");
        var syncPhase = string.IsNullOrWhiteSpace(phase) ? "sync" : phase.Trim().ToLowerInvariant();
        var resolvedSeverity = string.IsNullOrWhiteSpace(severity)
            ? (syncPhase.Contains("startup", StringComparison.OrdinalIgnoreCase) || syncPhase.Contains("post-login", StringComparison.OrdinalIgnoreCase) ? "Warning" : "Error")
            : severity.Trim();

        var entityName = string.Empty;
        var entityId = string.Empty;
        var referenceEntityName = string.Empty;
        var referenceEntityId = string.Empty;
        var reason = detail;

        var entityMatch = EntityConflictPattern.Match(detail);
        if (entityMatch.Success)
        {
            entityName = entityMatch.Groups["entity"].Value;
            entityId = entityMatch.Groups["entityId"].Value;
            reason = entityMatch.Groups["reason"].Value;
        }

        var referenceMatch = MissingReferencePattern.Match(reason);
        if (referenceMatch.Success)
        {
            referenceEntityName = referenceMatch.Groups["refEntity"].Value;
            referenceEntityId = referenceMatch.Groups["refId"].Value;
        }

        var deferredDirtyMatch = DeferredDirtyMissingCredentialPattern.Match(detail);
        if (deferredDirtyMatch.Success)
        {
            var scopeKey = deferredDirtyMatch.Groups["scope"].Value.Trim();
            var officeCode = deferredDirtyMatch.Groups["office"].Value.Trim();
            var tenantCode = deferredDirtyMatch.Groups["tenant"].Value.Trim();
            var targetDisplayName = ResolveScopeTargetDisplayName(scopeKey, officeCode, tenantCode);
            return new SyncDiagnosticClassification(
                syncPhase,
                resolvedSeverity,
                "권한/범위 오류",
                "missing_sync_credential",
                "PendingSyncScope",
                scopeKey,
                "Office",
                officeCode,
                $"pending_scope|missing_sync_credential|{scopeKey}|{officeCode}|{tenantCode}",
                false,
                $"환경설정 > 동기화에서 {targetDisplayName} 계정을 저장한 뒤 선택 범위 동기화를 다시 실행하세요.");
        }

        var remainingDirtyMatch = RemainingDirtyScopePattern.Match(detail);
        if (remainingDirtyMatch.Success)
        {
            var scopeKey = remainingDirtyMatch.Groups["scope"].Value.Trim();
            var officeCode = remainingDirtyMatch.Groups["office"].Value.Trim();
            var tenantCode = remainingDirtyMatch.Groups["tenant"].Value.Trim();
            return new SyncDiagnosticClassification(
                syncPhase,
                resolvedSeverity,
                "저장/동기화 오류",
                "remaining_dirty",
                "PendingSyncScope",
                scopeKey,
                "Office",
                officeCode,
                $"pending_scope|remaining_dirty|{scopeKey}|{officeCode}|{tenantCode}",
                true,
                "남은 dirty 보기 또는 선택 범위 동기화로 잔여 변경을 확인한 뒤 다시 동기화하세요.");
        }

        if (detail.Contains("cannot modify this office scope", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "권한/범위 오류", "office_scope", entityName, entityId, referenceEntityName, referenceEntityId, normalized, false, "담당지점/권한/연동 정책 설정을 확인하세요.");
        }

        if (referenceMatch.Success)
        {
            return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "참조 누락 오류", $"missing_{referenceEntityName.ToLowerInvariant()}", entityName, entityId, referenceEntityName, referenceEntityId, normalized, true, "자동 복구 실행 후 동기화를 다시 시도하세요.");
        }

        if (detail.Contains("DbUpdateConcurrencyException", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("affected 0 row(s)", StringComparison.OrdinalIgnoreCase)
            || exception is DbUpdateConcurrencyException)
        {
            return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "동시성 충돌", "db_concurrency", entityName, entityId, referenceEntityName, referenceEntityId, normalized, true, "공유 캐시 재구성 후 동기화를 다시 시도하세요.");
        }

        if (detail.Contains("A task was canceled", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || exception is TaskCanceledException
            || exception is TimeoutException)
        {
            return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "통신 오류", "network_timeout", entityName, entityId, referenceEntityName, referenceEntityId, normalized, true, "네트워크 상태를 확인한 뒤 동기화를 다시 시도하세요.");
        }

        if (detail.Contains("500 Internal Server Error", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("Internal Server Error", StringComparison.OrdinalIgnoreCase)
            || detail.Contains("could not be translated", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "서버 처리 오류", "server_failure", entityName, entityId, referenceEntityName, referenceEntityId, normalized, false, "서버 로그와 최근 배포 상태를 확인하세요.");
        }

        if (syncPhase.Contains("startup", StringComparison.OrdinalIgnoreCase)
            || syncPhase.Contains("post-login", StringComparison.OrdinalIgnoreCase)
            || syncPhase.Contains("shared-refresh", StringComparison.OrdinalIgnoreCase))
        {
            return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "시작 복구 오류", "startup_recovery", entityName, entityId, referenceEntityName, referenceEntityId, normalized, true, "앱을 재실행하거나 공유 캐시 재구성을 실행하세요.");
        }

        return new SyncDiagnosticClassification(syncPhase, resolvedSeverity, "저장/동기화 오류", "general_sync_failure", entityName, entityId, referenceEntityName, referenceEntityId, normalized, recoveryAttempted || recoverySucceeded, recoverySucceeded ? "자동 복구가 완료되었습니다." : "동기화 재시도 후 동일하면 진단 리포트를 저장하세요.");
    }

    private static string ResolveScopeTargetDisplayName(string scopeKey, string officeCode, string tenantCode)
    {
        if (!string.IsNullOrWhiteSpace(officeCode))
            return OfficeCodeCatalog.GetOfficeDisplayName(officeCode);

        if (scopeKey.StartsWith("TENANT:", StringComparison.OrdinalIgnoreCase))
        {
            var normalizedTenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(scopeKey[7..], tenantCode);
            if (!string.IsNullOrWhiteSpace(normalizedTenantCode))
                return TenantScopeCatalog.GetTenantDisplayName(normalizedTenantCode);
        }

        if (!string.IsNullOrWhiteSpace(tenantCode))
            return TenantScopeCatalog.GetTenantDisplayName(tenantCode);

        return string.IsNullOrWhiteSpace(scopeKey) ? "해당 범위" : scopeKey;
    }

    private sealed record SyncDiagnosticClassification(
        string SyncPhase,
        string Severity,
        string Category,
        string Subcategory,
        string EntityName,
        string EntityId,
        string ReferenceEntityName,
        string ReferenceEntityId,
        string NormalizedMessage,
        bool IsRecoverable,
        string RecoveryAction);
}
