using System.Text.Json;
using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ItemDuplicateMergeServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;

    public ItemDuplicateMergeServiceTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
    }

    [Fact]
    public async Task Merge_SucceedsAtomically_TombstonesDuplicate_MovesReferences_AndTouchesParents()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var customer = new Customer { NameOriginal = "merge customer", NameMatchKey = "MERGECUSTOMER" };
        var invoice = new Invoice { CustomerId = customer.Id, InvoiceNumber = "MERGE-1", VoucherType = VoucherType.Sales };
        var invoiceLine = new InvoiceLine
        {
            InvoiceId = invoice.Id, ItemId = duplicate.Id, ItemNameOriginal = duplicate.NameOriginal,
            Quantity = 1m, ItemTrackingType = ItemTrackingTypes.Stock
        };
        var asset = new RentalAsset { ItemId = duplicate.Id, ItemName = duplicate.NameOriginal, AssetKey = "merge-asset" };
        var history = new RentalAssetAssignmentHistory { AssetId = asset.Id, ItemName = duplicate.NameOriginal };
        var profile = new RentalBillingProfile
        {
            ProfileKey = "merge-profile",
            BillingTemplateJson = JsonSerializer.Serialize(new[] { new { CatalogItemId = duplicate.Id, DisplayItemName = duplicate.NameOriginal } })
        };
        var transfer = new InventoryTransfer
        {
            TransferNumber = "MERGE-T1",
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu
        };
        var transferLine = new InventoryTransferLine { TransferId = transfer.Id, ItemId = duplicate.Id, ItemNameOriginal = duplicate.NameOriginal };
        db.AddRange(customer, invoice, invoiceLine, asset, history, profile, transfer, transferLine);
        await db.SaveChangesAsync();
        var invoiceRevision = invoice.Revision;
        var transferRevision = transfer.Revision;

        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.True(preview.CanMerge, string.Join(" | ", preview.BlockingReasons));

        var result = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Success, result.Status);
        Assert.False(result.Result!.IsReplay);
        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == canonical.Id)).IsDeleted);
        Assert.True((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        Assert.Equal(canonical.Id, (await db.InvoiceLines.SingleAsync()).ItemId);
        Assert.Equal(canonical.Id, (await db.RentalAssets.IgnoreQueryFilters().SingleAsync()).ItemId);
        Assert.Equal(canonical.NameOriginal, (await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync()).ItemName);
        Assert.Contains(canonical.Id.ToString("D"), (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync()).BillingTemplateJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(canonical.Id, (await db.InventoryTransferLines.SingleAsync()).ItemId);
        var rebuiltLedger = await db.InventoryLedgerEntries.ToListAsync();
        Assert.NotEmpty(rebuiltLedger);
        Assert.All(rebuiltLedger, entry => Assert.Equal(canonical.Id, entry.ItemId));
        Assert.True((await db.Invoices.IgnoreQueryFilters().SingleAsync()).Revision > invoiceRevision);
        Assert.True((await db.InventoryTransfers.IgnoreQueryFilters().SingleAsync()).Revision > transferRevision);
        Assert.Single(await db.ProcessedSyncMutations.Where(row => row.EntityName == ItemDuplicateMergeService.ReceiptEntityName).ToListAsync());
    }

    [Fact]
    public async Task Merge_RewritesOnlyExactCaseInsensitiveTemplateReferences_AndPreservesDeletedAndFutureShape()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var unrelatedItemId = Guid.NewGuid();
        var templateItemId = Guid.NewGuid();
        var representativeAssetId = Guid.NewGuid();
        var includedAssetId = Guid.NewGuid();
        var activeProfileId = Guid.NewGuid();
        var deletedProfileId = Guid.NewGuid();
        var activeTemplate =
            "[{\"catalogitemid\":\"" + duplicate.Id.ToString("D") +
            "\",\"displayitemname\":\"legacy display\",\"SPECIFICATION\":\"legacy spec\",\"ItemId\":\"" + templateItemId.ToString("D") +
            "\",\"RepresentativeAssetId\":\"" + representativeAssetId.ToString("D") +
            "\",\"IncludedAssetIds\":[\"" + includedAssetId.ToString("D") +
            "\"],\"FutureTemplateProperty\":{\"Version\":2}},{\"CatalogItemId\":\"" + unrelatedItemId.ToString("D") +
            "\",\"DisplayItemName\":\"Atomic merge item\",\"Specification\":\"same specification\",\"FutureRow\":\"keep\"}]";
        var deletedTemplate =
            "[{\"CATALOGITEMID\":\"" + duplicate.Id.ToString("N").ToUpperInvariant() +
            "\",\"ItemId\":\"" + templateItemId.ToString("D") +
            "\",\"IncludedAssetIds\":[\"" + includedAssetId.ToString("D") + "\"],\"FutureDeletedProperty\":true}]";
        db.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = activeProfileId,
                ProfileKey = "merge-template-shape-active",
                BillingTemplateJson = activeTemplate
            },
            new RentalBillingProfile
            {
                Id = deletedProfileId,
                ProfileKey = "merge-template-shape-deleted",
                BillingTemplateJson = deletedTemplate,
                IsDeleted = true
            });
        await db.SaveChangesAsync();

        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        var duplicatePreview = Assert.Single(preview.Candidates, candidate => candidate.ItemId == duplicate.Id);
        Assert.Equal(2, duplicatePreview.RentalBillingTemplateCount);
        Assert.True(preview.CanMerge, string.Join(" | ", preview.BlockingReasons));

        var outcome = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Success, outcome.Status);
        Assert.Equal(2, outcome.Result!.UpdatedRentalBillingProfileCount);
        db.ChangeTracker.Clear();
        var storedActive = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == activeProfileId);
        using var activeDocument = JsonDocument.Parse(storedActive.BillingTemplateJson);
        var activeRows = activeDocument.RootElement.EnumerateArray().ToArray();
        Assert.Equal(2, activeRows.Length);
        Assert.Equal(canonical.Id.ToString("D"), activeRows[0].GetProperty("catalogitemid").GetString());
        Assert.Equal(canonical.NameOriginal, activeRows[0].GetProperty("displayitemname").GetString());
        Assert.Equal(canonical.SpecificationOriginal, activeRows[0].GetProperty("SPECIFICATION").GetString());
        Assert.Equal(templateItemId.ToString("D"), activeRows[0].GetProperty("ItemId").GetString());
        Assert.Equal(representativeAssetId.ToString("D"), activeRows[0].GetProperty("RepresentativeAssetId").GetString());
        Assert.Equal(includedAssetId.ToString("D"), Assert.Single(activeRows[0].GetProperty("IncludedAssetIds").EnumerateArray().ToArray()).GetString());
        Assert.Equal(2, activeRows[0].GetProperty("FutureTemplateProperty").GetProperty("Version").GetInt32());
        Assert.Equal(unrelatedItemId.ToString("D"), activeRows[1].GetProperty("CatalogItemId").GetString());
        Assert.Equal("keep", activeRows[1].GetProperty("FutureRow").GetString());

        var storedDeleted = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == deletedProfileId);
        Assert.True(storedDeleted.IsDeleted);
        using var deletedDocument = JsonDocument.Parse(storedDeleted.BillingTemplateJson);
        var deletedRow = Assert.Single(deletedDocument.RootElement.EnumerateArray().ToArray());
        Assert.Equal(canonical.Id.ToString("D"), deletedRow.GetProperty("CATALOGITEMID").GetString());
        Assert.Equal(templateItemId.ToString("D"), deletedRow.GetProperty("ItemId").GetString());
        Assert.Equal(includedAssetId.ToString("D"), Assert.Single(deletedRow.GetProperty("IncludedAssetIds").EnumerateArray().ToArray()).GetString());
        Assert.True(deletedRow.GetProperty("FutureDeletedProperty").GetBoolean());
        Assert.False(deletedRow.TryGetProperty("DisplayItemName", out _));
        Assert.False(deletedRow.TryGetProperty("Specification", out _));
        Assert.False(deletedRow.TryGetProperty("RepresentativeAssetId", out _));
    }

    [Theory]
    [InlineData(false, "malformed-d")]
    [InlineData(false, "malformed-n")]
    [InlineData(true, "malformed-d")]
    [InlineData(true, "malformed-n")]
    [InlineData(false, "malformed-unicode-d")]
    [InlineData(false, "non-array-unicode-n")]
    [InlineData(true, "malformed-unicode-d")]
    [InlineData(true, "non-array-unicode-n")]
    [InlineData(false, "malformed-whitespace-x")]
    [InlineData(true, "malformed-whitespace-x")]
    [InlineData(false, "non-array-unicode-whitespace-x")]
    [InlineData(true, "non-array-unicode-whitespace-x")]
    public async Task PreviewAndMerge_BlockCandidateGuidInUnsupportedActiveOrDeletedTemplate_WithoutWrites(
        bool isDeleted,
        string templateShape)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var profileId = Guid.NewGuid();
        var useCompactGuid = templateShape.EndsWith("-n", StringComparison.Ordinal);
        var candidateGuid = (useCompactGuid
                ? duplicate.Id.ToString("N")
                : duplicate.Id.ToString("D"))
            .ToUpperInvariant();
        if (templateShape.Contains("whitespace-x", StringComparison.Ordinal))
        {
            candidateGuid = duplicate.Id.ToString("X")
                .Replace("{", "{ ", StringComparison.Ordinal)
                .Replace("}", " }", StringComparison.Ordinal)
                .Replace(",", " , ", StringComparison.Ordinal);
        }
        if (templateShape.Contains("unicode", StringComparison.Ordinal))
        {
            candidateGuid = string.Concat(
                candidateGuid.Select(character => $"\\u{(int)character:x4}"));
        }

        var unsupportedTemplate = templateShape.StartsWith("non-array", StringComparison.Ordinal)
            ? $"{{\"catalogitemid\":\"{candidateGuid}\",\"FutureRoot\":true}}"
            : $"[{{\"catalogitemid\":\"{candidateGuid}\",\"Broken\":]";
        db.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            ProfileKey = $"candidate-unsupported-{isDeleted}-{templateShape}",
            BillingTemplateJson = unsupportedTemplate,
            IsDeleted = isDeleted
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, user);

        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.False(preview.CanMerge);
        Assert.Contains(preview.BlockingReasons, reason => reason.Contains("malformed", StringComparison.OrdinalIgnoreCase));
        var outcome = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Conflict, outcome.Status);
        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(isDeleted, storedProfile.IsDeleted);
        Assert.Equal(unsupportedTemplate, storedProfile.BillingTemplateJson);
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Theory]
    [InlineData(false, "known-catalog")]
    [InlineData(true, "known-catalog")]
    [InlineData(false, "null-catalog")]
    [InlineData(true, "null-catalog")]
    [InlineData(false, "missing-catalog")]
    [InlineData(true, "missing-catalog")]
    public async Task Merge_BlocksCandidateTokenOutsideKnownCatalogItemId_WithoutWrites(
        bool isDeleted,
        string templateShape)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var profileId = Guid.NewGuid();
        var futureReference = duplicate.Id.ToString("D");
        var templateJson = templateShape switch
        {
            "known-catalog" =>
                "[{\"CatalogItemId\":\"" + duplicate.Id.ToString("D") +
                "\",\"DisplayItemName\":\"legacy\",\"Specification\":\"legacy spec\",\"FutureItemReference\":\"" +
                futureReference + "\",\"FutureRow\":{\"Keep\":true}}]",
            "null-catalog" =>
                "[{\"CatalogItemId\":null,\"FutureItemReference\":\"" + futureReference +
                "\",\"FutureRow\":{\"Keep\":true}}]",
            "missing-catalog" =>
                "[{\"FutureItemReference\":\"" + futureReference +
                "\",\"FutureRow\":{\"Keep\":true}}]",
            _ => throw new ArgumentOutOfRangeException(nameof(templateShape), templateShape, null)
        };
        db.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            ProfileKey = $"residual-candidate-{templateShape}-{isDeleted}",
            BillingTemplateJson = templateJson,
            IsDeleted = isDeleted
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.False(preview.CanMerge);
        Assert.Contains(
            preview.BlockingReasons,
            reason => reason.Contains("outside", StringComparison.OrdinalIgnoreCase));

        var outcome = await service.MergeAsync(
            Command(preview, canonical.Id, duplicate.Id),
            CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Conflict, outcome.Status);
        Assert.Equal("merge_blocked", outcome.Error);
        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == canonical.Id)).IsDeleted);
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters()
            .SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(isDeleted, storedProfile.IsDeleted);
        Assert.Equal(templateJson, storedProfile.BillingTemplateJson);
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Theory]
    [InlineData("catalog", false)]
    [InlineData("catalog", true)]
    [InlineData("display", false)]
    [InlineData("display", true)]
    [InlineData("specification", false)]
    [InlineData("specification", true)]
    public async Task PreviewAndMerge_BlockAmbiguousCaseInsensitiveTemplateProperties_WithoutWrites(
        string ambiguousProperty,
        bool isDeleted)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var profileId = Guid.NewGuid();
        var ambiguousTemplate = ambiguousProperty switch
        {
            "catalog" =>
                "[{\"CatalogItemId\":\"" + duplicate.Id.ToString("D") +
                "\",\"catalogitemid\":\"" + canonical.Id.ToString("D") + "\"}]",
            "display" =>
                "[{\"CatalogItemId\":\"" + duplicate.Id.ToString("D") +
                "\",\"DisplayItemName\":\"first\",\"displayitemname\":\"second\"}]",
            _ =>
                "[{\"CatalogItemId\":\"" + duplicate.Id.ToString("D") +
                "\",\"Specification\":\"first\",\"SPECIFICATION\":\"second\"}]"
        };
        db.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            ProfileKey = $"ambiguous-template-{ambiguousProperty}-{isDeleted}",
            BillingTemplateJson = ambiguousTemplate,
            IsDeleted = isDeleted
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, user);

        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.False(preview.CanMerge);
        Assert.Contains(preview.BlockingReasons, reason => reason.Contains("unsupported", StringComparison.OrdinalIgnoreCase));
        var outcome = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Conflict, outcome.Status);
        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(isDeleted, storedProfile.IsDeleted);
        Assert.Equal(ambiguousTemplate, storedProfile.BillingTemplateJson);
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Merge_AllowsUnrelatedMalformedActiveOrDeletedTemplate_AndPreservesOriginal(bool isDeleted)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var unrelatedId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var malformedTemplate = "[{\"CatalogItemId\":\"" + unrelatedId.ToString("D") + "\",\"Broken\":]";
        db.RentalBillingProfiles.Add(new RentalBillingProfile
        {
            Id = profileId,
            ProfileKey = $"unrelated-malformed-{isDeleted}",
            BillingTemplateJson = malformedTemplate,
            IsDeleted = isDeleted
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.True(preview.CanMerge, string.Join(" | ", preview.BlockingReasons));

        var outcome = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Success, outcome.Status);
        db.ChangeTracker.Clear();
        var storedProfile = await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync(profile => profile.Id == profileId);
        Assert.Equal(isDeleted, storedProfile.IsDeleted);
        Assert.Equal(malformedTemplate, storedProfile.BillingTemplateJson);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("canonical")]
    [InlineData("collision")]
    public async Task PreviewAndMerge_BlockSoftDeletedPriceGradeReferences_AndIncludeCountsAndToken(string mode)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var option = new PriceGradeOption { Name = $"deleted-grade-{mode}", IsActive = true };
        db.PriceGradeOptions.Add(option);
        if (mode is "duplicate" or "collision")
        {
            db.ItemPriceGrades.Add(new ItemPriceGrade
            {
                ItemId = duplicate.Id,
                PriceGradeOptionId = option.Id,
                PriceGradeName = option.Name,
                IsActive = false,
                IsDeleted = true
            });
        }
        if (mode is "canonical" or "collision")
        {
            db.ItemPriceGrades.Add(new ItemPriceGrade
            {
                ItemId = canonical.Id,
                PriceGradeOptionId = option.Id,
                PriceGradeName = option.Name,
                IsActive = false,
                IsDeleted = true
            });
        }
        await db.SaveChangesAsync();
        var service = CreateService(db, user);

        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.False(preview.CanMerge);
        Assert.Contains(preview.BlockingReasons, reason => reason.Contains("price grades", StringComparison.OrdinalIgnoreCase));
        Assert.False(string.IsNullOrWhiteSpace(preview.ServerSnapshotToken));
        Assert.Equal(mode is "canonical" or "collision" ? 1 : 0,
            Assert.Single(preview.Candidates, candidate => candidate.ItemId == canonical.Id).ItemPriceGradeCount);
        Assert.Equal(mode is "duplicate" or "collision" ? 1 : 0,
            Assert.Single(preview.Candidates, candidate => candidate.ItemId == duplicate.Id).ItemPriceGradeCount);
        var deletedGrade = await db.ItemPriceGrades.IgnoreQueryFilters().OrderBy(grade => grade.Id).FirstAsync();
        deletedGrade.UnitPrice = 1234m;
        await db.SaveChangesAsync();
        var refreshedPreview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.NotEqual(preview.ServerSnapshotToken, refreshedPreview.ServerSnapshotToken);
        var outcome = await service.MergeAsync(Command(refreshedPreview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Conflict, outcome.Status);
        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        Assert.Equal(mode == "collision" ? 2 : 1, await db.ItemPriceGrades.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Fact]
    public async Task Merge_ReplaysSameMutation_AndRejectsDifferentPayload()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        await db.SaveChangesAsync();
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        var command = Command(preview, canonical.Id, duplicate.Id);

        Assert.Equal(ItemDuplicateMergeStatus.Success, (await service.MergeAsync(command, CancellationToken.None)).Status);
        var replay = await service.MergeAsync(command, CancellationToken.None);
        Assert.Equal(ItemDuplicateMergeStatus.Success, replay.Status);
        Assert.True(replay.Result!.IsReplay);

        command.ExpectedServerSnapshotToken = new string('a', 64);
        var reuse = await service.MergeAsync(command, CancellationToken.None);
        Assert.Equal(ItemDuplicateMergeStatus.Conflict, reuse.Status);
        Assert.Equal("mutation_id_conflict", reuse.Error);
        Assert.Single(await db.ProcessedSyncMutations.ToListAsync());
    }

    [Theory]
    [InlineData("semantic")]
    [InlineData("current-stock")]
    [InlineData("warehouse-stock")]
    [InlineData("warehouse-offset")]
    [InlineData("price-grade")]
    [InlineData("active-editor")]
    [InlineData("malformed-template")]
    public async Task Preview_BlocksUnsafeGroups(string mode)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        switch (mode)
        {
            case "semantic": duplicate.Unit = "BOX"; break;
            case "current-stock": duplicate.CurrentStock = 1m; break;
            case "warehouse-stock": db.ItemWarehouseStocks.Add(new ItemWarehouseStock { ItemId = duplicate.Id, WarehouseCode = "USENET-MAIN", Quantity = 1m }); break;
            case "warehouse-offset":
                db.ItemWarehouseStocks.AddRange(
                    new ItemWarehouseStock { ItemId = duplicate.Id, WarehouseCode = "USENET-MAIN", Quantity = 5m },
                    new ItemWarehouseStock { ItemId = duplicate.Id, WarehouseCode = "USENET-SECONDARY", Quantity = -5m });
                break;
            case "price-grade":
                var option = new PriceGradeOption { Name = "merge grade", IsActive = true };
                db.Add(option);
                db.ItemPriceGrades.Add(new ItemPriceGrade { ItemId = duplicate.Id, PriceGradeOptionId = option.Id, PriceGradeName = option.Name, IsActive = true });
                break;
            case "active-editor": db.ActiveEditSessions.Add(new ActiveEditSession { EntityType = "Item", EntityId = duplicate.Id.ToString("D"), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(1) }); break;
            case "malformed-template": db.RentalBillingProfiles.Add(new RentalBillingProfile { ProfileKey = "bad-template", BillingTemplateJson = $"[{{\"CatalogItemId\":\"{duplicate.Id:D}\"" }); break;
        }
        await db.SaveChangesAsync();

        var preview = await CreateService(db, user).PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.False(preview.CanMerge);
        Assert.NotEmpty(preview.BlockingReasons);
    }

    [Fact]
    public async Task Preview_BlocksMissingSideEffectPermission_AndCandidateWriteScope()
    {
        var limited = new TestCurrentUserContext
        {
            Permissions = [PermissionNames.ItemEdit],
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };
        await using var db = CreateDb(limited);
        var (canonical, duplicate) = AddPair(db);
        var customer = new Customer { NameOriginal = "permission customer", NameMatchKey = "PERMISSIONCUSTOMER" };
        var invoice = new Invoice { CustomerId = customer.Id, InvoiceNumber = "PERMISSION-1" };
        db.AddRange(customer, invoice, new InvoiceLine { InvoiceId = invoice.Id, ItemId = duplicate.Id });
        await db.SaveChangesAsync();

        var preview = await CreateService(db, limited).PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.False(preview.CanMerge);
        Assert.Contains(preview.BlockingReasons, reason => reason.Contains("InvoiceEdit", StringComparison.Ordinal));

        canonical.OfficeCode = OfficeCodeCatalog.Yeonsu;
        duplicate.OfficeCode = OfficeCodeCatalog.Yeonsu;
        await db.SaveChangesAsync();
        preview = await CreateService(db, limited).PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.Contains(preview.BlockingReasons, reason => reason.Contains("Item write scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Merge_PreservesNonDefaultValues_AndRemapsZeroWarehouseRows()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        canonical.SimpleMemo = string.Empty;
        canonical.PurchasePrice = 0m;
        duplicate.SimpleMemo = "preserve this memo";
        duplicate.PurchasePrice = 123m;
        db.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = duplicate.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 0m
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.True(preview.CanMerge, string.Join(" | ", preview.BlockingReasons));

        var outcome = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.Equal(ItemDuplicateMergeStatus.Success, outcome.Status);
        db.ChangeTracker.Clear();
        var storedCanonical = await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == canonical.Id);
        Assert.Equal("preserve this memo", storedCanonical.SimpleMemo);
        Assert.Equal(123m, storedCanonical.PurchasePrice);
        var stock = await db.ItemWarehouseStocks.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(canonical.Id, stock.ItemId);
        Assert.Equal(0m, stock.Quantity);
    }

    [Fact]
    public async Task Preview_UsesTrimmedOrdinalExactGroup_NotLooseMatchKey()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var canonical = NewItem();
        canonical.NameOriginal = "AB-CD";
        var duplicate = NewItem();
        duplicate.NameOriginal = " AB-CD ";
        var looseCollision = NewItem();
        looseCollision.NameOriginal = "ABCD";
        db.AddRange(canonical, duplicate, looseCollision);
        await db.SaveChangesAsync();

        var preview = await CreateService(db, user).PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.True(preview.CanMerge, string.Join(" | ", preview.BlockingReasons));
        Assert.Equal(2, preview.Candidates.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreviewAndMerge_KeepSharedAndConcreteOfficeItemsInSeparateGroups(
        bool canonicalIsShared)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var sharedItem = NewItem();
        sharedItem.OfficeCode = OfficeCodeCatalog.Shared;
        var concreteItem = NewItem();
        concreteItem.OfficeCode = OfficeCodeCatalog.Usenet;
        db.Items.AddRange(sharedItem, concreteItem);
        await db.SaveChangesAsync();

        var canonical = canonicalIsShared ? sharedItem : concreteItem;
        var duplicate = canonicalIsShared ? concreteItem : sharedItem;
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(
            Preview(canonical.Id, duplicate.Id),
            CancellationToken.None);

        Assert.False(preview.CanMerge);
        Assert.Contains(
            preview.BlockingReasons,
            reason => reason.Contains("exact tenant/office/name/specification group", StringComparison.Ordinal));

        var outcome = await service.MergeAsync(
            Command(preview, canonical.Id, duplicate.Id),
            CancellationToken.None);

        Assert.NotEqual(ItemDuplicateMergeStatus.Success, outcome.Status);
        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == sharedItem.Id)).IsDeleted);
        Assert.False((await db.Items.IgnoreQueryFilters()
            .SingleAsync(item => item.Id == concreteItem.Id)).IsDeleted);
    }

    [Fact]
    public async Task PreviewEndpoint_ForbidsOutOfScopeCandidates_WithoutReturningPreviewPayload()
    {
        var limited = new TestCurrentUserContext
        {
            Permissions = [PermissionNames.ItemEdit],
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet
        };
        await using var db = CreateDb(limited);
        var (canonical, duplicate) = AddPair(db);
        canonical.OfficeCode = OfficeCodeCatalog.Yeonsu;
        duplicate.OfficeCode = OfficeCodeCatalog.Yeonsu;
        await db.SaveChangesAsync();
        var scope = new OfficeScopeService(limited, db);
        var controller = new ItemsController(db, scope, new ItemDuplicateMergeService(db, scope, limited));

        var response = await controller.PreviewDuplicateMerge(Preview(canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Null(response.Value);
    }

    [Fact]
    public async Task PreviewEndpoint_ForbidsDeletedCandidate_WithoutReturningPreviewPayload()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        await db.SaveChangesAsync();
        duplicate.IsDeleted = true;
        await db.SaveChangesAsync();
        var scope = new OfficeScopeService(user, db);
        var controller = new ItemsController(db, scope, new ItemDuplicateMergeService(db, scope, user));

        var response = await controller.PreviewDuplicateMerge(Preview(canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.IsType<ForbidResult>(response.Result);
        Assert.Null(response.Value);
    }

    [Theory]
    [InlineData("invoice")]
    [InlineData("transfer")]
    public async Task Preview_BlocksReferenceWithMissingParent(string referenceKind)
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=OFF;");
        if (referenceKind == "invoice")
        {
            db.InvoiceLines.Add(new InvoiceLine
            {
                InvoiceId = Guid.NewGuid(),
                ItemId = duplicate.Id,
                ItemNameOriginal = duplicate.NameOriginal
            });
        }
        else
        {
            db.InventoryTransferLines.Add(new InventoryTransferLine
            {
                TransferId = Guid.NewGuid(),
                ItemId = duplicate.Id,
                ItemNameOriginal = duplicate.NameOriginal
            });
        }
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys=ON;");

        var preview = await CreateService(db, user).PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);

        Assert.False(preview.CanMerge);
        Assert.Contains(preview.BlockingReasons, reason => reason.Contains("no parent", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Merge_RejectsStaleToken_AndCanonicalOutsideGroup()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        await db.SaveChangesAsync();
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        duplicate.Notes = "changed after preview";
        await db.SaveChangesAsync();

        var stale = await service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None);
        Assert.Equal(ItemDuplicateMergeStatus.Conflict, stale.Status);
        Assert.Equal("stale_snapshot", stale.Error);

        var outside = await service.PreviewAsync(new ItemDuplicateMergePreviewRequestDto
        {
            CandidateItemIds = [canonical.Id, duplicate.Id],
            CanonicalItemId = Guid.NewGuid()
        }, CancellationToken.None);
        Assert.False(outside.CanMerge);
        Assert.Contains(outside.BlockingReasons, reason => reason.Contains("outside", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Merge_FaultBeforeSave_RollsBackItemsReferencesAndReceipt()
    {
        var user = Admin();
        await using var db = CreateDb(user);
        var (canonical, duplicate) = AddPair(db);
        var customer = new Customer { NameOriginal = "rollback customer", NameMatchKey = "ROLLBACKCUSTOMER" };
        var invoice = new Invoice { CustomerId = customer.Id, InvoiceNumber = "ROLLBACK-1" };
        var line = new InvoiceLine { InvoiceId = invoice.Id, ItemId = duplicate.Id };
        var asset = new RentalAsset { ItemId = duplicate.Id, ItemName = duplicate.NameOriginal, AssetKey = "rollback-asset" };
        var history = new RentalAssetAssignmentHistory { AssetId = asset.Id, ItemName = duplicate.NameOriginal };
        var profile = new RentalBillingProfile
        {
            ProfileKey = "rollback-profile",
            BillingTemplateJson = JsonSerializer.Serialize(new[] { new { CatalogItemId = duplicate.Id } })
        };
        var transfer = new InventoryTransfer
        {
            TransferNumber = "ROLLBACK-T1",
            SourceOfficeCode = OfficeCodeCatalog.Usenet,
            TargetOfficeCode = OfficeCodeCatalog.Yeonsu
        };
        var transferLine = new InventoryTransferLine { TransferId = transfer.Id, ItemId = duplicate.Id };
        var stock = new ItemWarehouseStock
        {
            ItemId = duplicate.Id,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 0m
        };
        db.AddRange(customer, invoice, line, asset, history, profile, transfer, transferLine, stock);
        await db.SaveChangesAsync();
        var auditCountBefore = await db.AuditLogs.CountAsync();
        var originalTemplate = profile.BillingTemplateJson;
        var service = CreateService(db, user);
        var preview = await service.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        service.TestOnlyAfterSaveBeforeCommitAsync = _ => throw new InvalidOperationException("injected rollback");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.MergeAsync(Command(preview, canonical.Id, duplicate.Id), CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.False((await db.Items.IgnoreQueryFilters().SingleAsync(item => item.Id == duplicate.Id)).IsDeleted);
        Assert.Equal(duplicate.Id, (await db.InvoiceLines.SingleAsync()).ItemId);
        Assert.Equal(duplicate.Id, (await db.RentalAssets.IgnoreQueryFilters().SingleAsync()).ItemId);
        Assert.Equal(duplicate.NameOriginal, (await db.RentalAssetAssignmentHistories.IgnoreQueryFilters().SingleAsync()).ItemName);
        Assert.Equal(originalTemplate, (await db.RentalBillingProfiles.IgnoreQueryFilters().SingleAsync()).BillingTemplateJson);
        Assert.Equal(duplicate.Id, (await db.InventoryTransferLines.SingleAsync()).ItemId);
        Assert.Equal(duplicate.Id, (await db.ItemWarehouseStocks.IgnoreQueryFilters().SingleAsync()).ItemId);
        Assert.Empty(await db.ProcessedSyncMutations.ToListAsync());
        Assert.Empty(await db.InventoryLedgerEntries.ToListAsync());
        Assert.Equal(auditCountBefore, await db.AuditLogs.CountAsync());
    }

    [Fact]
    public async Task Merge_AcrossSqliteContexts_WaitsForSerializedTransaction_ThenReplays()
    {
        var databaseName = $"item-duplicate-merge-lock-{Guid.NewGuid():N}";
        var connectionString = $"Data Source={databaseName};Mode=Memory;Cache=Shared";
        await using var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connectionString).Options;
        var firstUser = Admin();
        var secondUser = Admin();
        await using var firstDb = new AppDbContext(options, firstUser, new RevisionClock());
        await using var secondDb = new AppDbContext(options, secondUser, new RevisionClock());
        await firstDb.Database.EnsureCreatedAsync();
        var (canonical, duplicate) = AddPair(firstDb);
        await firstDb.SaveChangesAsync();
        var firstService = CreateService(firstDb, firstUser);
        var secondService = CreateService(secondDb, secondUser);
        var preview = await firstService.PreviewAsync(Preview(canonical.Id, duplicate.Id), CancellationToken.None);
        var command = Command(preview, canonical.Id, duplicate.Id);
        var enteredBeforeCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        firstService.TestOnlyAfterSaveBeforeCommitAsync = async cancellationToken =>
        {
            enteredBeforeCommit.TrySetResult();
            await releaseCommit.Task.WaitAsync(cancellationToken);
        };

        var firstTask = firstService.MergeAsync(command, CancellationToken.None);
        await enteredBeforeCommit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var secondTask = secondService.MergeAsync(command, CancellationToken.None);
        await Task.Delay(150);
        Assert.False(secondTask.IsCompleted);
        releaseCommit.TrySetResult();

        var first = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
        var second = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ItemDuplicateMergeStatus.Success, first.Status);
        Assert.Equal(ItemDuplicateMergeStatus.Success, second.Status);
        Assert.True(second.Result!.IsReplay);
        Assert.Equal(1, await secondDb.ProcessedSyncMutations.CountAsync());
    }

    private AppDbContext CreateDb(TestCurrentUserContext user)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        var db = new AppDbContext(options, user, new RevisionClock());
        db.Database.EnsureCreated();
        return db;
    }

    private static ItemDuplicateMergeService CreateService(AppDbContext db, TestCurrentUserContext user)
    {
        var scope = new OfficeScopeService(user, db);
        return new ItemDuplicateMergeService(db, scope, user);
    }

    private static (Item Canonical, Item Duplicate) AddPair(AppDbContext db)
    {
        var canonical = NewItem();
        var duplicate = NewItem();
        db.Items.AddRange(canonical, duplicate);
        return (canonical, duplicate);
    }

    private static Item NewItem() => new()
    {
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet,
        NameOriginal = "Atomic merge item",
        NameMatchKey = "ATOMICMERGEITEM",
        SpecificationOriginal = "same specification",
        SpecificationMatchKey = "SAMESPECIFICATION",
        CategoryName = "office",
        ItemKind = ItemKinds.Product,
        TrackingType = ItemTrackingTypes.Stock,
        Unit = "EA",
        IsSale = true
    };

    private static ItemDuplicateMergePreviewRequestDto Preview(Guid canonicalId, Guid duplicateId)
        => new() { CandidateItemIds = [canonicalId, duplicateId], CanonicalItemId = canonicalId };

    private static ItemDuplicateMergeRequestDto Command(ItemDuplicateMergePreviewDto preview, Guid canonicalId, Guid duplicateId)
        => new()
        {
            CandidateItemIds = [canonicalId, duplicateId],
            CanonicalItemId = canonicalId,
            ExpectedServerSnapshotToken = preview.ServerSnapshotToken,
            MutationId = $"item-duplicate-merge:{canonicalId:N}"
        };

    private static TestCurrentUserContext Admin() => new()
    {
        IsAdmin = true,
        ScopeType = TenantScopeCatalog.ScopeAdmin,
        TenantCode = TenantScopeCatalog.UsenetGroup,
        OfficeCode = OfficeCodeCatalog.Usenet
    };

    public void Dispose() => _connection.Dispose();

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = "item-duplicate-merge-test";
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = [];
        public bool HasPermission(string permission) => IsAdmin || IsGodMode || Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }
}
