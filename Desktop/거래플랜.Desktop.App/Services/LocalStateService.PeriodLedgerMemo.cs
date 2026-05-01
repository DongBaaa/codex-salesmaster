using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

public sealed partial class LocalStateService
{
    public async Task<OfficeMutationResult> UpdateInvoiceLedgerMemoAsync(
        Guid invoiceId,
        string? memo,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanSaveInvoices(session))
            return OfficeMutationResult.Denied("현재 계정은 전표메모를 수정할 권한이 없습니다.");

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted && i.IsLatestVersion, ct);

        if (invoice is null)
            return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");

        if (!CanAccessInvoice(invoice, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 전표메모를 수정할 수 없습니다.");

        var normalizedMemo = memo ?? string.Empty;
        if (string.Equals(invoice.Memo ?? string.Empty, normalizedMemo, StringComparison.Ordinal))
            return OfficeMutationResult.Ok(invoice.Id, "변경된 전표메모가 없습니다.");

        var now = DateTime.UtcNow;
        var beforeMemo = invoice.Memo ?? string.Empty;

        invoice.Memo = normalizedMemo;
        invoice.IsDirty = true;
        invoice.UpdatedAtUtc = now;
        invoice.LastSavedAtUtc = now;
        invoice.LastSavedByUsername = session.User?.Username ?? "local-user";
        invoice.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = "LocalInvoice",
            EntityId = invoice.Id.ToString("D"),
            Action = "UpdateLedgerMemo",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? "user",
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(new { Memo = beforeMemo }, AuditJsonOptions),
            AfterJson = JsonSerializer.Serialize(new { Memo = normalizedMemo }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(invoice.Id, "전표메모를 저장했습니다.");
    }

    public async Task<OfficeMutationResult> UpdateInvoiceLineLedgerMemoAsync(
        Guid invoiceId,
        Guid lineId,
        string? itemMemo,
        SessionState session,
        CancellationToken ct = default)
    {
        if (!CanSaveInvoices(session))
            return OfficeMutationResult.Denied("현재 계정은 품목비고를 수정할 권한이 없습니다.");

        var invoice = await _db.Invoices
            .IgnoreQueryFilters()
            .Include(i => i.Lines)
            .FirstOrDefaultAsync(i => i.Id == invoiceId && !i.IsDeleted && i.IsLatestVersion, ct);

        if (invoice is null)
            return OfficeMutationResult.Missing("전표를 찾을 수 없습니다.");

        if (!CanAccessInvoice(invoice, session))
            return OfficeMutationResult.Denied("권한이 없어 해당 품목비고를 수정할 수 없습니다.");

        var line = invoice.Lines.FirstOrDefault(l => l.Id == lineId && !l.IsDeleted);
        if (line is null)
            return OfficeMutationResult.Missing("수정할 품목 행을 찾을 수 없습니다.");

        var normalizedMemo = itemMemo ?? string.Empty;
        if (string.Equals(line.Remark ?? string.Empty, normalizedMemo, StringComparison.Ordinal))
            return OfficeMutationResult.Ok(line.Id, "변경된 품목비고가 없습니다.");

        var now = DateTime.UtcNow;
        var beforeMemo = line.Remark ?? string.Empty;

        line.Remark = normalizedMemo;
        invoice.IsDirty = true;
        invoice.UpdatedAtUtc = now;
        invoice.LastSavedAtUtc = now;
        invoice.LastSavedByUsername = session.User?.Username ?? "local-user";
        invoice.ConcurrencyStamp = Guid.NewGuid().ToString("N");

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = "LocalInvoiceLine",
            EntityId = line.Id.ToString("D"),
            Action = "UpdateLedgerItemMemo",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? "user",
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(new { InvoiceId = invoice.Id, Remark = beforeMemo }, AuditJsonOptions),
            AfterJson = JsonSerializer.Serialize(new { InvoiceId = invoice.Id, Remark = normalizedMemo }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(line.Id, "품목비고를 저장했습니다.");
    }

    public async Task<OfficeMutationResult> UpdatePaymentLedgerMemoAsync(
        Guid paymentId,
        string? memo,
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return OfficeMutationResult.Denied("로그인한 계정만 수금/지급 메모를 수정할 수 있습니다.");

        var payment = await _db.Payments
            .IgnoreQueryFilters()
            .Include(p => p.Invoice)
            .FirstOrDefaultAsync(p => p.Id == paymentId && !p.IsDeleted, ct);

        if (payment is null)
            return OfficeMutationResult.Missing("수금/지급 기록을 찾을 수 없습니다.");

        if (payment.Invoice is null)
            return OfficeMutationResult.Missing("연결된 전표를 찾을 수 없습니다.");

        if (!CanWriteOfficeScope(session, payment.Invoice.ResponsibleOfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 메모를 수정할 수 없습니다.");

        var normalizedMemo = memo ?? string.Empty;
        if (string.Equals(payment.Note ?? string.Empty, normalizedMemo, StringComparison.Ordinal))
            return OfficeMutationResult.Ok(payment.Id, "변경된 수금/지급 메모가 없습니다.");

        var now = DateTime.UtcNow;
        var beforeMemo = payment.Note ?? string.Empty;

        payment.Note = normalizedMemo;
        payment.IsDirty = true;
        payment.UpdatedAtUtc = now;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = "LocalPayment",
            EntityId = payment.Id.ToString("D"),
            Action = "UpdateLedgerMemo",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? "user",
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(new { Note = beforeMemo }, AuditJsonOptions),
            AfterJson = JsonSerializer.Serialize(new { Note = normalizedMemo }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(payment.Id, "수금/지급 메모를 저장했습니다.");
    }

    public async Task<OfficeMutationResult> UpdateTransactionLedgerMemoAsync(
        Guid transactionId,
        string? memo,
        SessionState session,
        CancellationToken ct = default)
    {
        if (session is null || !session.IsLoggedIn)
            return OfficeMutationResult.Denied("로그인한 계정만 수금/지급 전표메모를 수정할 수 있습니다.");

        var transaction = await _db.Transactions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == transactionId && !t.IsDeleted, ct);

        if (transaction is null)
            return OfficeMutationResult.Missing("수금/지급 내역을 찾을 수 없습니다.");

        if (!CanWriteOfficeScope(session, transaction.ResponsibleOfficeCode))
            return OfficeMutationResult.Denied("권한이 없어 해당 수금/지급 전표메모를 수정할 수 없습니다.");

        var normalizedMemo = memo ?? string.Empty;
        if (string.Equals(transaction.Memo ?? string.Empty, normalizedMemo, StringComparison.Ordinal))
            return OfficeMutationResult.Ok(transaction.Id, "변경된 수금/지급 전표메모가 없습니다.");

        var now = DateTime.UtcNow;
        var beforeMemo = transaction.Memo ?? string.Empty;

        transaction.Memo = normalizedMemo;
        transaction.IsDirty = true;
        transaction.UpdatedAtUtc = now;

        _db.AuditLogs.Add(new LocalAuditLog
        {
            EntityName = "LocalTransaction",
            EntityId = transaction.Id.ToString("D"),
            Action = "UpdateLedgerMemo",
            Username = session.User?.Username ?? "local-user",
            Role = session.User?.Role ?? "user",
            OfficeCode = session.OfficeCode,
            BeforeJson = JsonSerializer.Serialize(new { Memo = beforeMemo }, AuditJsonOptions),
            AfterJson = JsonSerializer.Serialize(new { Memo = normalizedMemo }, AuditJsonOptions),
            CreatedAtUtc = now
        });

        await _db.SaveChangesAsync(ct);
        return OfficeMutationResult.Ok(transaction.Id, "수금/지급 전표메모를 저장했습니다.");
    }
}
