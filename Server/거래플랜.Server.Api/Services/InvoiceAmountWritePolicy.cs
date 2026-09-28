using Microsoft.EntityFrameworkCore;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Services;

/// <summary>
/// Resolves money for an already scope-validated invoice command. Mutation receipts
/// must retain the incoming payload hash, before this policy normalizes the DTO.
/// </summary>
internal static class InvoiceAmountWritePolicy
{
    internal sealed record Result(string? Error, decimal? Total = null, decimal Supply = 0, decimal Vat = 0)
    {
        public bool Calculated => Total.HasValue;

        public void ApplyTo(Invoice invoice)
        {
            if (!Total.HasValue) return;
            invoice.TotalAmount = Total.Value;
            invoice.SupplyAmount = Supply;
            invoice.VatAmount = Vat;
        }
    }

    public static async Task<Result> NormalizeAsync(AppDbContext db, ICurrentUserContext user,
        OfficeScopeService scope, InvoiceDto dto, Invoice? existing, CancellationToken ct)
    {
        if (dto.IsDeleted) return new(null);
        if (!Enum.IsDefined(dto.VoucherType))
            return new("전표 종류를 확인해 주세요.");

        // Checking both types prevents switching a protected sales invoice to a
        // purchase invoice just to supply its money through another permission.
        if (CanView(user, dto.VoucherType) && (existing is null || CanView(user, existing.VoucherType)))
            return dto.AmountsHidden
                ? new("금액이 비공개 상태인 전표입니다. 현재 권한으로 전표를 다시 조회한 뒤 저장해 주세요.")
                : new(null);

        var basis = existing;
        if (basis is null && dto.PreviousVersionId is { } previousId && previousId != Guid.Empty)
        {
            basis = db.Invoices.Local.FirstOrDefault(x => x.Id == previousId && !x.IsDeleted);
            basis ??= await scope.ApplyInvoiceScope(db.Invoices.AsNoTracking())
                .Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == previousId, ct);
            if (basis is null || basis.CustomerId != dto.CustomerId ||
                !scope.CanReadOfficeForInvoices(basis.ResponsibleOfficeCode, basis.TenantCode, basis.OfficeCode) ||
                basis.TenantCode != dto.TenantCode || basis.OfficeCode != dto.OfficeCode ||
                basis.ResponsibleOfficeCode != dto.ResponsibleOfficeCode ||
                (basis.VersionGroupId == Guid.Empty ? basis.Id : basis.VersionGroupId) != dto.VersionGroupId)
                return new("이전 전표의 금액 기준을 확인할 수 없습니다. 전표를 새로 조회해 주세요.");
        }

        if (basis is not null && (basis.CustomerId != dto.CustomerId || basis.VoucherType != dto.VoucherType ||
            InvoiceVatModes.Normalize(basis.VatMode) != InvoiceVatModes.Normalize(dto.VatMode)))
            return new("금액 조회 권한이 없는 계정은 기존 전표의 거래처·종류·부가세 방식을 변경할 수 없습니다.");

        var lines = (dto.Lines ?? []).Where(x => !x.IsDeleted).ToList();
        if (lines.Where(x => x.Id != Guid.Empty).GroupBy(x => x.Id).Any(x => x.Count() > 1))
            return new("중복된 전표 행이 있습니다. 전표를 새로 조회해 주세요.");
        var previousLines = (basis?.Lines ?? []).Where(x => !x.IsDeleted).ToList();
        var unmatched = previousLines.ToList();
        var explicitLineIds = lines.Select(x => x.Id).Where(x => x != Guid.Empty).ToHashSet();
        var resolved = new List<(InvoiceLineDto Line, decimal Price, decimal Amount)>();
        var unchanged = basis is not null && lines.Count == previousLines.Count;

        var itemIds = lines.Where(x => x.ItemId.HasValue).Select(x => x.ItemId!.Value).Distinct().ToList();
        var items = await scope.ApplySyncItemScope(db.Items.AsNoTracking())
            .Where(x => itemIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == dto.CustomerId, ct);
        if (customer is null || !scope.CanWriteOfficeForCustomers(customer.ResponsibleOfficeCode, customer.TenantCode, customer.OfficeCode))
            return new("거래처의 금액 기준을 확인할 수 없습니다.");
        var grade = (customer.PriceGrade ?? string.Empty).Trim();
        var options = await db.PriceGradeOptions.AsNoTracking().Where(x => x.IsActive).ToListAsync(ct);
        var grades = await db.ItemPriceGrades.AsNoTracking()
            .Where(x => x.IsActive && itemIds.Contains(x.ItemId)).ToListAsync(ct);

        var purchaseLike = IsPurchaseLike(dto.VoucherType);
        // Persisted server invoices are the confirmed price history. Restrict it
        // by invoice read scope, even if the same customer spans multiple offices.
        var purchasePrices = purchaseLike
            ? await scope.ApplyInvoiceScope(db.Invoices.AsNoTracking())
                .Where(x => x.IsLatestVersion && x.VoucherType == VoucherType.Purchase &&
                    x.CustomerId == dto.CustomerId && x.Id != dto.Id)
                .SelectMany(x => x.Lines.Where(l => !l.IsDeleted && l.ItemId.HasValue &&
                        itemIds.Contains(l.ItemId.Value) && l.UnitPrice > 0),
                    (x, l) => new { ItemId = l.ItemId!.Value, l.UnitPrice, x.InvoiceDate, x.UpdatedAtUtc, l.Id })
                .ToListAsync(ct)
            : [];
        try
        {
            foreach (var line in lines)
            {
                var prior = unmatched.FirstOrDefault(x => x.Id == line.Id && SameItem(x, line));
                if (prior is null && basis is not null)
                {
                    // Desktop revisions generate new line ids. Preserve an old
                    // price only when its item identity has one unambiguous match.
                    var candidates = unmatched.Where(x => !explicitLineIds.Contains(x.Id) && SameItem(x, line)).ToList();
                    var indistinguishableIncoming = candidates.Count == 1
                        ? lines.Count(x => !previousLines.Any(p => p.Id == x.Id) && SameItem(candidates[0], x))
                        : 0;
                    if (candidates.Count > 1 || indistinguishableIncoming > 1)
                        return new("동일 품목의 서로 다른 기존 행을 구분할 수 없습니다. 금액 권한이 있는 담당자가 확인해 주세요.");
                    prior = candidates.SingleOrDefault();
                }

                decimal price;
                decimal amount;
                if (prior is not null)
                {
                    unmatched.Remove(prior);
                    price = prior.UnitPrice;
                    amount = prior.Quantity == line.Quantity
                        ? (prior.LineAmount == 0 ? prior.Quantity * prior.UnitPrice : prior.LineAmount)
                        : line.Quantity * price;
                    unchanged &= prior.Quantity == line.Quantity;
                }
                else
                {
                    unchanged = false;
                    if (line.ItemId is not { } itemId || !items.TryGetValue(itemId, out var item))
                        return new("자동 금액 계산을 위해 조회 가능한 등록 품목을 선택해 주세요.");
                    if (UnitCatalogNormalizer.Normalize(line.Unit) != UnitCatalogNormalizer.Normalize(item.Unit))
                        return new("자동 금액 계산 시 품목에 등록된 단위를 사용해 주세요.");
                    if (purchaseLike)
                    {
                        price = purchasePrices.Where(x => x.ItemId == itemId)
                            .OrderByDescending(x => x.InvoiceDate).ThenByDescending(x => x.UpdatedAtUtc)
                            .ThenBy(x => x.Id).Select(x => x.UnitPrice).FirstOrDefault();
                        if (price <= 0) price = item.PurchasePrice > 0 ? item.PurchasePrice : DefaultSalesPrice(item);
                    }
                    else
                    {
                        var customPrices = grades.Where(x => x.ItemId == itemId &&
                            SameText(x.PriceGradeName.Trim(), grade) && x.UnitPrice > 0)
                            .Select(x => x.UnitPrice).Distinct().ToList();
                        var sources = options.Where(x => SameText(x.Name.Trim(), grade))
                            .Select(x => NormalizeSource(x.PriceSource)).Distinct().ToList();
                        if (customPrices.Count > 1 || sources.Count > 1)
                            return new("거래처 단가 설정이 중복되어 자동 계산할 수 없습니다. 단가 설정을 확인해 주세요.");
                        price = customPrices.Count == 1 ? customPrices[0]
                            : ResolveSalesPrice(item, sources.SingleOrDefault() ?? LegacySource(grade));
                    }
                    if (price <= 0)
                        return new("품목의 적용 단가가 등록되어 있지 않습니다. 금액 권한이 있는 담당자가 단가를 확인해 주세요.");
                    amount = Math.Round(line.Quantity * price, 2, MidpointRounding.AwayFromZero);
                }
                amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
                if (Math.Abs(price) >= 10000000000000000m || Math.Abs(amount) >= 10000000000000000m)
                    return new("수량과 단가의 계산 범위를 초과했습니다. 품목과 수량을 확인해 주세요.");
                resolved.Add((line, price, amount));
            }

            var totals = unchanged && unmatched.Count == 0
                ? (SupplyAmount: basis!.SupplyAmount, VatAmount: basis.VatAmount, TotalAmount: basis.TotalAmount)
                : InvoiceVatModes.CalculateTotals(resolved.Select(x => x.Amount), dto.VatMode);
            if (Math.Abs(totals.TotalAmount) >= 10000000000000000m ||
                Math.Abs(totals.SupplyAmount) >= 10000000000000000m || Math.Abs(totals.VatAmount) >= 10000000000000000m)
                return new("전표 합계의 계산 범위를 초과했습니다. 품목과 수량을 확인해 주세요.");
            foreach (var row in resolved)
            {
                row.Line.UnitPrice = row.Price;
                row.Line.LineAmount = row.Amount;
            }
            dto.TotalAmount = totals.TotalAmount;
            dto.SupplyAmount = totals.SupplyAmount;
            dto.VatAmount = totals.VatAmount;
            return new(null, totals.TotalAmount, totals.SupplyAmount, totals.VatAmount);
        }
        catch (OverflowException)
        {
            return new("수량과 단가의 계산 범위를 초과했습니다. 품목과 수량을 확인해 주세요.");
        }
    }

    private static bool CanView(ICurrentUserContext user, VoucherType type)
        => user.HasPermission(IsPurchaseLike(type) ? PermissionNames.AmountViewPurchase : PermissionNames.AmountViewSales);

    private static bool IsPurchaseLike(VoucherType type)
        => type is VoucherType.Purchase or VoucherType.Procurement or VoucherType.Expense;

    private static bool SameItem(InvoiceLine old, InvoiceLineDto line)
        => old.ItemId == line.ItemId && SameText(old.ItemNameOriginal, line.ItemNameOriginal) &&
            SameText(old.SpecificationOriginal, line.SpecificationOriginal) &&
            UnitCatalogNormalizer.Normalize(old.Unit) == UnitCatalogNormalizer.Normalize(line.Unit) &&
            SameText(old.SerialNumber, line.SerialNumber) && SameText(old.MaterialNumber, line.MaterialNumber) &&
            old.RentalStartDate == line.RentalStartDate && old.RentalEndDate == line.RentalEndDate;

    private static bool SameText(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static decimal DefaultSalesPrice(Item item) => item.SalePrice > 0 ? item.SalePrice : item.RetailPrice;
    private static decimal ResolveSalesPrice(Item item, string source) => source switch
    {
        "A" when item.PriceGradeA > 0 => item.PriceGradeA,
        "B" when item.PriceGradeB > 0 => item.PriceGradeB,
        "C" when item.PriceGradeC > 0 => item.PriceGradeC,
        "RETAIL" when item.RetailPrice > 0 => item.RetailPrice,
        _ => DefaultSalesPrice(item)
    };
    private static string NormalizeSource(string source) => source.Trim().ToUpperInvariant() switch
    {
        "A" => "A", "B" => "B", "C" => "C", "RETAIL" or "소매" => "RETAIL", _ => "SALES"
    };
    private static string LegacySource(string grade)
    {
        var value = grade.ToUpperInvariant();
        if (value.StartsWith('A')) return "A";
        if (value.StartsWith('B')) return "B";
        if (value.StartsWith('C')) return "C";
        return value.Contains("소매") ? "RETAIL" : "SALES";
    }
}
