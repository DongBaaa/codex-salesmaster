using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// A snapshot is a response copy, not the persisted conflict audit or a business entity.
internal static class TransactionAmountReadPolicy
{
    private static readonly JsonSerializerOptions SnapshotJson = new() { PropertyNameCaseInsensitive = true };
    private static readonly HashSet<string> MoneyProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "SettlementAmount", "AdvanceDelta", "PrepaidDelta", "CashReceipt", "CardReceipt",
        "BankReceipt", "DiscountApplied", "ReceiptTotal", "CashPayment", "CardPayment",
        "BankPayment", "DiscountReceived", "PaymentTotal"
    };

    internal static bool CanView(TransactionDto dto, VoucherType? linkedType, OfficeScopeService scope)
    {
        if (scope.CanViewSalesAmounts() && scope.CanViewPurchaseAmounts()) return true;
        var kind = dto.TransactionKind?.Trim();
        var sales = kind is "일반수금" or "전표수금" or "선수금입금" or "선수금환불" or "선수금차감" or "렌탈수금";
        var purchase = kind is "일반지급" or "전표지급";
        if (!sales && !purchase) return false;
        if (dto.LinkedInvoiceId is Guid id && id != Guid.Empty)
        {
            if (!InvoiceAmountReadPolicy.CanView(linkedType, scope)) return false;
            if (sales != (linkedType is VoucherType.Sales or VoucherType.Collection)) return false;
        }
        // Historical mixed-direction data needs both grants. An advance refund is
        // a sales-side refund even though its cash moves out through payment fields.
        if (sales && (NonZero(dto.PrepaidDelta) || kind != "선수금환불" &&
            new[] { dto.CashPayment, dto.CardPayment, dto.BankPayment, dto.DiscountReceived, dto.PaymentTotal }.Any(NonZero)))
            return false;
        if (purchase && new[] { dto.AdvanceDelta, dto.CashReceipt, dto.CardReceipt,
                dto.BankReceipt, dto.DiscountApplied, dto.ReceiptTotal }.Any(NonZero))
            return false;
        return sales ? scope.CanViewSalesAmounts() : scope.CanViewPurchaseAmounts();
    }

    private static bool NonZero(decimal? value) => value is null || value != 0m;

    public static async Task ApplyAsync(IReadOnlyCollection<TransactionDto> transactions,
        AppDbContext db, OfficeScopeService scope, CancellationToken ct)
    {
        if (scope.CanViewSalesAmounts() && scope.CanViewPurchaseAmounts()) return;
        var types = await LoadTypesAsync(transactions.Select(x => x.LinkedInvoiceId), db, ct);
        foreach (var dto in transactions)
        {
            VoucherType? type = dto.LinkedInvoiceId is Guid id && types.TryGetValue(id, out var value) ? value : null;
            if (CanView(dto, type, scope)) continue;
            dto.SettlementAmount = dto.AdvanceDelta = dto.PrepaidDelta = null;
            dto.CashReceipt = dto.CardReceipt = dto.BankReceipt = dto.DiscountApplied = dto.ReceiptTotal = null;
            dto.CashPayment = dto.CardPayment = dto.BankPayment = dto.DiscountReceived = dto.PaymentTotal = null;
        }
    }

    private static async Task<Dictionary<Guid, VoucherType>> LoadTypesAsync(IEnumerable<Guid?> invoiceIds,
        AppDbContext db, CancellationToken ct)
    {
        var types = new Dictionary<Guid, VoucherType>();
        // Only the type of a link on an already scope-filtered response is read.
        foreach (var ids in invoiceIds.OfType<Guid>().Where(id => id != Guid.Empty).Distinct().Chunk(500))
            foreach (var row in await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                         .Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.VoucherType }).ToListAsync(ct))
                types[row.Id] = row.VoucherType;
        return types;
    }

    public static async Task ApplyConflictsAsync(IReadOnlyCollection<ConflictLogDto> conflicts,
        AppDbContext db, OfficeScopeService scope, CancellationToken ct)
    {
        if (scope.CanViewSalesAmounts() && scope.CanViewPurchaseAmounts()) return;
        var snapshots = new List<(ConflictLogDto Conflict, bool Server, JsonObject Json, TransactionDto Dto)>();
        foreach (var conflict in conflicts.Where(c => c.EntityName is "TransactionRecord" or "Transaction"))
        foreach (var server in new[] { false, true })
        {
            var raw = server ? conflict.ServerJson : conflict.ClientJson;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                if (JsonNode.Parse(raw) is not JsonObject json) throw new JsonException();
                var dto = JsonSerializer.Deserialize<TransactionDto>(raw, SnapshotJson) ?? throw new JsonException();
                snapshots.Add((conflict, server, json, dto));
            }
            catch (JsonException)
            {
                if (server) conflict.ServerJson = string.Empty;
                else conflict.ClientJson = string.Empty;
            }
        }
        var types = await LoadTypesAsync(snapshots.Select(s => s.Dto.LinkedInvoiceId), db, ct);
        foreach (var snapshot in snapshots)
        {
            VoucherType? type = snapshot.Dto.LinkedInvoiceId is Guid id && types.TryGetValue(id, out var value) ? value : null;
            if (CanView(snapshot.Dto, type, scope)) continue;
            HideMoney(snapshot.Json);
            // Legacy omissions must not deserialize into known zero amounts.
            foreach (var name in MoneyProperties)
                if (!snapshot.Json.Any(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)))
                    snapshot.Json[name] = null;
            if (snapshot.Server) snapshot.Conflict.ServerJson = snapshot.Json.ToJsonString();
            else snapshot.Conflict.ClientJson = snapshot.Json.ToJsonString();
        }
    }

    private static void HideMoney(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(p => p.Key).ToArray())
                if (MoneyProperties.Contains(key)) obj[key] = null;
                else HideMoney(obj[key]);
        else if (node is JsonArray array)
            foreach (var child in array) HideMoney(child);
    }
}
