using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

// Response-only projection. Never change tracked business entities or conflict audit records.
internal static class InvoiceAmountReadPolicy
{
    public static bool CanView(VoucherType? type, OfficeScopeService scope) => type switch
    {
        VoucherType.Sales or VoucherType.Collection => scope.CanViewSalesAmounts(),
        VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense => scope.CanViewPurchaseAmounts(),
        _ => false
    };

    public static InvoiceDto Apply(InvoiceDto dto, OfficeScopeService scope)
    {
        if (CanView(dto.VoucherType, scope)) return dto;
        dto.TotalAmount = dto.SupplyAmount = dto.VatAmount = null;
        foreach (var line in dto.Lines ?? []) line.UnitPrice = line.LineAmount = null;
        foreach (var payment in dto.Payments ?? []) payment.Amount = null;
        return dto;
    }

    public static async Task ApplyPaymentsAsync(IReadOnlyCollection<PaymentDto> payments,
        AppDbContext db, OfficeScopeService scope, CancellationToken ct)
    {
        var types = await LoadTypesAsync(payments.Select(x => x.InvoiceId), db, ct);
        foreach (var payment in payments)
            if (!types.TryGetValue(payment.InvoiceId, out var type) || !CanView(type, scope))
                payment.Amount = null;
    }

    private static async Task<Dictionary<Guid, VoucherType>> LoadTypesAsync(IEnumerable<Guid> invoiceIds,
        AppDbContext db, CancellationToken ct)
    {
        var result = new Dictionary<Guid, VoucherType>();
        // This lookup selects only a type for already scope-filtered response identities.
        foreach (var batch in invoiceIds.Where(id => id != Guid.Empty).Distinct().Chunk(500))
            foreach (var row in await db.Invoices.IgnoreQueryFilters().AsNoTracking()
                         .Where(invoice => batch.Contains(invoice.Id))
                         .Select(invoice => new { invoice.Id, invoice.VoucherType }).ToListAsync(ct))
                result[row.Id] = row.VoucherType;
        return result;
    }

    public static async Task ApplyConflictsAsync(IReadOnlyCollection<ConflictLogDto> conflicts,
        AppDbContext db, OfficeScopeService scope, CancellationToken ct)
    {
        if (scope.CanViewSalesAmounts() && scope.CanViewPurchaseAmounts()) return;
        var snapshots = new List<(ConflictLogDto Conflict, bool Server, JsonObject Json, string Entity)>();
        foreach (var conflict in conflicts)
        {
            if (conflict.EntityName is not ("Invoice" or "Payment")) continue;
            foreach (var server in new[] { false, true })
            {
                var raw = server ? conflict.ServerJson : conflict.ClientJson;
                if (string.IsNullOrWhiteSpace(raw)) continue;
                try
                {
                    if (JsonNode.Parse(raw) is not JsonObject json) throw new JsonException();
                    snapshots.Add((conflict, server, json, conflict.EntityName));
                }
                catch (JsonException)
                {
                    // An unparseable snapshot cannot prove its disclosure scope.
                    if (server) conflict.ServerJson = string.Empty;
                    else conflict.ClientJson = string.Empty;
                }
            }
        }
        var types = await LoadTypesAsync(snapshots.Where(x => x.Entity == "Payment")
            .Select(x => ReadGuid(x.Json, "InvoiceId")), db, ct);
        foreach (var snapshot in snapshots)
        {
            VoucherType? type = null;
            if (snapshot.Entity == "Invoice")
            {
                var value = Property(snapshot.Json, "VoucherType");
                try { if (value is not null) type = JsonSerializer.Deserialize<VoucherType>(value.ToJsonString()); }
                catch (JsonException) { /* Unknown kind stays undisclosed. */ }
            }
            else if (types.TryGetValue(ReadGuid(snapshot.Json, "InvoiceId"), out var parentType)) type = parentType;
            if (CanView(type, scope)) continue;
            HideMoney(snapshot.Json);
            if (snapshot.Server) snapshot.Conflict.ServerJson = snapshot.Json.ToJsonString();
            else snapshot.Conflict.ClientJson = snapshot.Json.ToJsonString();
        }
    }

    private static JsonNode? Property(JsonObject json, string name)
        => json.FirstOrDefault(x => string.Equals(x.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    private static Guid ReadGuid(JsonObject json, string name)
        => Property(json, name) is JsonValue value && value.TryGetValue<string>(out var text) && Guid.TryParse(text, out var id) ? id : Guid.Empty;
    private static readonly HashSet<string> MoneyProperties = new(StringComparer.OrdinalIgnoreCase)
        { "UnitPrice", "LineAmount", "SupplyAmount", "VatAmount", "TotalAmount", "Amount" };
    private static void HideMoney(JsonNode? node)
    {
        if (node is JsonObject obj)
            foreach (var key in obj.Select(x => x.Key).ToArray())
                if (MoneyProperties.Contains(key)) obj[key] = null;
                else HideMoney(obj[key]);
        else if (node is JsonArray array)
            foreach (var child in array) HideMoney(child);
    }
}
