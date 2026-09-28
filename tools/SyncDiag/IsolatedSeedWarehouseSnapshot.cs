using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Tools.SyncDiag;

// Used only while preparing a leased private copy for a fresh test server.
// The quantities are the complete current snapshot, including posted transfers.
internal static class IsolatedSeedWarehouseSnapshot
{
    internal static async Task<List<ItemWarehouseStockDto>> BootstrapAsync(
        LocalDbContext db, ErpApiClient api, CancellationToken ct = default)
    {
        var stocks = await db.ItemWarehouseStocks.ToListAsync(ct);
        var expected = stocks.Select(LocalMappings.ToDto).ToList();
        if (stocks.Count == 0) return expected;
        var ids = stocks.Select(x => x.ItemId).Distinct().ToList();
        var items = await db.Items.IgnoreQueryFilters().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, ct);
        var categories = await db.ItemCategoryOptions.IgnoreQueryFilters().Where(x=>!x.IsDeleted).ToListAsync(ct);
        if (items.Count != ids.Count || items.Values.Any(x => x.IsDeleted))
            throw new InvalidOperationException("Invalid isolated warehouse snapshot item or quantity.");
        var lineQuantities = await db.InvoiceLines.Where(x => x.ItemId.HasValue && ids.Contains(x.ItemId.Value) && !x.IsDeleted)
            .Select(x => new { ItemId=x.ItemId!.Value, x.Quantity }).ToListAsync(ct);
        var transferQuantities = await db.InventoryTransferLines.Where(x => x.ItemId.HasValue && ids.Contains(x.ItemId.Value) && !x.IsDeleted)
            .Select(x => new { ItemId=x.ItemId!.Value, x.Quantity }).ToListAsync(ct);
        var replayReserve = lineQuantities.Concat(transferQuantities).GroupBy(x=>x.ItemId)
            .ToDictionary(g=>g.Key,g=>g.Sum(x=>Math.Abs(x.Quantity)));
        foreach (var group in stocks.GroupBy(x=>items[x.ItemId].TenantCode))
        {
            var business = group.Key switch { "USENET_GROUP"=>"USENET", "ITWORLD"=>"ITWORLD",
                _=>throw new InvalidOperationException("Unknown isolated warehouse tenant.") };
            var before = await api.PullAsync(0,business,ct) ?? throw new InvalidOperationException("Missing isolated server snapshot.");
            var groupIds=group.Select(x=>x.ItemId).ToHashSet();
            if (before.ItemWarehouseStocks.Any() || before.Items.Any(x=>groupIds.Contains(x.Id)))
                throw new InvalidOperationException("Warehouse bootstrap requires a fresh isolated server.");
            // Seed the original category IDs before item import can auto-create
            // categories with new IDs and conflict with the later full snapshot.
            var request = new SyncPushRequest { DeviceId="isolated-warehouse-bootstrap",
                ItemCategoryOptions=categories.Select(LocalMappings.ToDto).ToList() };
            foreach(var id in groupIds)
            {
                var dto=LocalMappings.ToDto(items[id]);
                dto.Revision=0; dto.ExpectedRevision=0; dto.CurrentStock=0;
                request.Items.Add(dto);
            }
            foreach(var stock in group)
            {
                var dto=LocalMappings.ToDto(stock);
                dto.Revision=0; dto.ExpectedRevision=0;
                // This private server must validate historical outgoing documents
                // before the same push installs the final current snapshot. The
                // reserve is temporary; final seed verification requires the exact
                // original quantities before the runtime can be certified.
                dto.Quantity=Math.Max(0m,stock.Quantity)+replayReserve.GetValueOrDefault(stock.ItemId);
                request.ItemWarehouseStocks.Add(dto);
            }
            var result=await api.PushAsync(request,business,ct) ?? throw new InvalidOperationException("Missing isolated bootstrap acknowledgement.");
            if(result.ConflictCount!=0 || result.AcceptedItemWarehouseStockKeys.Count!=group.Count())
                throw new InvalidOperationException("Isolated warehouse bootstrap was not fully acknowledged.");
            var after=await api.PullAsync(0,business,ct) ?? throw new InvalidOperationException("Missing isolated bootstrap verification.");
            foreach(var stock in group)
            {
                var actual=after.ItemWarehouseStocks.Single(x=>x.ItemId==stock.ItemId && x.WarehouseCode==stock.WarehouseCode);
                if(actual.Quantity!=request.ItemWarehouseStocks.Single(x=>x.ItemId==stock.ItemId && x.WarehouseCode==stock.WarehouseCode).Quantity || actual.Revision<=0)
                    throw new InvalidOperationException("Isolated warehouse bootstrap snapshot mismatch.");
                stock.Revision=actual.Revision;
            }
            foreach(var id in groupIds) items[id].Revision=after.Items.Single(x=>x.Id==id).Revision;
            foreach(var category in categories) category.Revision=after.ItemCategoryOptions.Single(x=>x.Id==category.Id).Revision;
        }
        await db.SaveChangesAsync(ct);
        return expected;
    }

    internal static async Task VerifyFinalAsync(LocalDbContext db,
        IReadOnlyList<ItemWarehouseStockDto> expected, CancellationToken ct=default)
    {
        var actual=await db.ItemWarehouseStocks.AsNoTracking().ToListAsync(ct);
        if(actual.Count!=expected.Count || expected.Any(e=>!actual.Any(a=>a.ItemId==e.ItemId && a.WarehouseCode==e.WarehouseCode && a.Quantity==e.Quantity)))
            throw new InvalidOperationException("Final isolated warehouse quantities differ from the source snapshot.");
    }

    internal static async Task VerifyServerFinalAsync(LocalDbContext db, ErpApiClient api,
        IReadOnlyList<ItemWarehouseStockDto> expected, CancellationToken ct=default)
    {
        var ids=expected.Select(x=>x.ItemId).Distinct().ToList();
        var tenants=await db.Items.IgnoreQueryFilters().Where(x=>ids.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.TenantCode,ct);
        foreach(var group in expected.GroupBy(x=>tenants[x.ItemId]))
        {
            var business=group.Key switch {"USENET_GROUP"=>"USENET","ITWORLD"=>"ITWORLD",_=>throw new InvalidOperationException("Unknown seed tenant.")};
            var actual=await api.PullAsync(0,business,ct) ?? throw new InvalidOperationException("Missing final seed server verification.");
            foreach(var stock in group)
                if(actual.ItemWarehouseStocks.Count(x=>x.ItemId==stock.ItemId && x.WarehouseCode==stock.WarehouseCode && x.Quantity==stock.Quantity)!=1)
                    throw new InvalidOperationException("Final isolated server still differs from source warehouse quantities.");
        }
    }

    internal static async Task<int> PrepareForFreshServerAsync(LocalDbContext db,
        CancellationToken ct = default)
    {
        if (await db.ItemWarehouseStocks.AnyAsync(x => x.Revision < 0, ct))
            throw new InvalidOperationException("Invalid warehouse revision in isolated seed snapshot.");

        // A revision from another database asserts that the row already exists.
        // Preserve quantities and timestamps, but identify these as new rows so
        // the server accepts the snapshot before applying historical documents.
        return await db.ItemWarehouseStocks.Where(x => x.Revision > 0)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Revision, 0L), ct);
    }
}
