using 거래플랜.Server.Api.Controllers;
using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Security;
using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class SyncInvoiceDeletePreservationTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"georaeplan-sync-invoice-delete-{Guid.NewGuid():N}.db");

    public async Task InitializeAsync()
    {
        await using var dbContext = CreateDbContext(CreateAdminUser());
        await dbContext.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _databasePath, $"{_databasePath}-wal", $"{_databasePath}-shm" })
        {
            if (File.Exists(path))
                File.Delete(path);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task Push_DeleteExistingInvoice_WithWritableAttackDto_PreservesSnapshotAndDuplicateReceipt()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var seed = await SeedRichInvoiceAsync(dbContext);
        var controller = CreateSyncController(dbContext, currentUser);
        var request = CreateAttackDeleteRequest(seed, seed.AlternateCustomerId, "delete-rich-invoice");

        var firstResult = AssertOk(await controller.Push(request, CancellationToken.None));

        Assert.Equal(1, firstResult.AcceptedCount);
        Assert.Equal(0, firstResult.ConflictCount);
        Assert.Equal(0, firstResult.DuplicateMutationCount);

        dbContext.ChangeTracker.Clear();
        var deletedInvoice = await LoadInvoiceAsync(dbContext, seed.InvoiceId);
        Assert.True(deletedInvoice.IsDeleted);
        Assert.True(deletedInvoice.Revision > seed.Revision);
        AssertBusinessSnapshot(seed.Snapshot, deletedInvoice);
        Assert.False(deletedInvoice.IsLatestVersion);
        Assert.All(deletedInvoice.Lines, line => Assert.True(line.IsDeleted));

        var deletedRevision = deletedInvoice.Revision;
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations.AsNoTracking()
                .CountAsync(receipt => receipt.MutationId == request.Invoices[0].MutationId));

        var retryResult = AssertOk(await controller.Push(request, CancellationToken.None));

        Assert.Equal(1, retryResult.AcceptedCount);
        Assert.Equal(0, retryResult.ConflictCount);
        Assert.Equal(1, retryResult.DuplicateMutationCount);
        dbContext.ChangeTracker.Clear();
        var afterRetry = await LoadInvoiceAsync(dbContext, seed.InvoiceId);
        Assert.Equal(deletedRevision, afterRetry.Revision);
        Assert.True(afterRetry.IsDeleted);
        AssertBusinessSnapshot(seed.Snapshot, afterRetry);
        Assert.False(afterRetry.IsLatestVersion);
        Assert.All(afterRetry.Lines, line => Assert.True(line.IsDeleted));
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations.AsNoTracking()
                .CountAsync(receipt => receipt.MutationId == request.Invoices[0].MutationId));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("outside")]
    [InlineData("empty")]
    public async Task Push_DeleteExistingInvoice_IgnoresIncomingCustomerValidityAndPreservesSnapshot(
        string customerCase)
    {
        var currentUser = CreateOfficeInvoiceEditor();
        await using var dbContext = CreateDbContext(currentUser);
        var seed = await SeedRichInvoiceAsync(dbContext);
        var incomingCustomerId = customerCase switch
        {
            "outside" => seed.OutsideCustomerId,
            "empty" => Guid.Empty,
            _ => Guid.NewGuid()
        };
        var controller = CreateSyncController(dbContext, currentUser);
        var request = CreateAttackDeleteRequest(
            seed,
            incomingCustomerId,
            $"delete-customer-{customerCase}");

        var result = AssertOk(await controller.Push(request, CancellationToken.None));

        Assert.Equal(1, result.AcceptedCount);
        Assert.Equal(0, result.ConflictCount);
        dbContext.ChangeTracker.Clear();
        var deletedInvoice = await LoadInvoiceAsync(dbContext, seed.InvoiceId);
        Assert.True(deletedInvoice.IsDeleted);
        AssertBusinessSnapshot(seed.Snapshot, deletedInvoice);
        Assert.False(deletedInvoice.IsLatestVersion);
        Assert.All(deletedInvoice.Lines, line => Assert.True(line.IsDeleted));
    }

    [Fact]
    public async Task Push_DeleteThenRecycleRestore_RestoresLinesStockAndLedgerFromOriginalSnapshot()
    {
        var currentUser = CreateAdminUser();
        await using var dbContext = CreateDbContext(currentUser);
        var seed = await SeedRichInvoiceAsync(dbContext);
        var syncController = CreateSyncController(dbContext, currentUser);
        var request = CreateAttackDeleteRequest(seed, seed.AlternateCustomerId, "delete-then-restore");

        var deleteResult = AssertOk(await syncController.Push(request, CancellationToken.None));
        Assert.Equal(1, deleteResult.AcceptedCount);
        Assert.Equal(0, deleteResult.ConflictCount);

        dbContext.ChangeTracker.Clear();
        var deletedInvoice = await LoadInvoiceAsync(dbContext, seed.InvoiceId);
        Assert.True(deletedInvoice.IsDeleted);
        AssertBusinessSnapshot(seed.Snapshot, deletedInvoice);
        Assert.False(deletedInvoice.IsLatestVersion);
        Assert.All(deletedInvoice.Lines, line => Assert.True(line.IsDeleted));
        Assert.Equal(100m, await LoadWarehouseQuantityAsync(dbContext, seed.ItemId));
        Assert.Equal(100m, await LoadItemCurrentStockAsync(dbContext, seed.ItemId));
        Assert.Equal(
            0,
            await dbContext.InventoryLedgerEntries.AsNoTracking()
                .CountAsync(entry => entry.SourceDocumentId == seed.InvoiceId));

        var revisionClock = new RevisionClock();
        var scopeService = new OfficeScopeService(currentUser, dbContext);
        var recycleController = new RecycleBinController(
            dbContext,
            scopeService,
            NoOpStoredFileReferenceReconciler.Instance,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalSettlementRecalculationService(dbContext),
            NoOpStoredFileDeferredDeletionQueue.Instance);
        var restoreResponse = await recycleController.Restore(
            new RecycleBinMutationRequest
            {
                Items =
                [
                    new RecycleBinMutationTargetDto
                    {
                        EntityId = seed.InvoiceId,
                        Kind = "invoice",
                        ExpectedRevision = deletedInvoice.Revision
                    }
                ]
            },
            CancellationToken.None);
        var restoreOk = Assert.IsType<OkObjectResult>(restoreResponse.Result);
        var restoreResult = Assert.IsType<RecycleBinMutationResultDto>(restoreOk.Value);

        Assert.Equal(1, restoreResult.SucceededCount);
        dbContext.ChangeTracker.Clear();
        var restoredInvoice = await LoadInvoiceAsync(dbContext, seed.InvoiceId);
        Assert.False(restoredInvoice.IsDeleted);
        AssertBusinessSnapshot(seed.Snapshot, restoredInvoice);
        Assert.True(restoredInvoice.IsLatestVersion);
        Assert.All(restoredInvoice.Lines, line => Assert.False(line.IsDeleted));
        Assert.Equal(92m, await LoadWarehouseQuantityAsync(dbContext, seed.ItemId));
        Assert.Equal(92m, await LoadItemCurrentStockAsync(dbContext, seed.ItemId));

        var ledgerEntry = Assert.Single(await dbContext.InventoryLedgerEntries.AsNoTracking()
            .Where(entry => entry.SourceDocumentId == seed.InvoiceId)
            .ToListAsync());
        Assert.Equal(seed.LineId, ledgerEntry.SourceLineId);
        Assert.Equal(-8m, ledgerEntry.QuantityDelta);
        Assert.Equal(OfficeCodeCatalog.UsenetMainWarehouse, ledgerEntry.WarehouseCode);
        Assert.Equal(
            1,
            await dbContext.ProcessedSyncMutations.AsNoTracking()
                .CountAsync(receipt => receipt.MutationId == request.Invoices[0].MutationId));
    }

    private async Task<SeededInvoice> SeedRichInvoiceAsync(AppDbContext dbContext)
    {
        var customerId = Guid.NewGuid();
        var alternateCustomerId = Guid.NewGuid();
        var outsideCustomerId = Guid.NewGuid();
        var itemId = Guid.NewGuid();
        var invoiceId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var alternateProfileId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var invoiceDate = new DateOnly(2026, 6, 18);

        dbContext.Customers.AddRange(
            new Customer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Original customer",
                NameMatchKey = "ORIGINALCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            },
            new Customer
            {
                Id = alternateCustomerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "Writable attack customer",
                NameMatchKey = "WRITABLEATTACKCUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            },
            new Customer
            {
                Id = outsideCustomerId,
                TenantCode = TenantScopeCatalog.Itworld,
                OfficeCode = OfficeCodeCatalog.Itworld,
                ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                NameOriginal = "Outside customer",
                NameMatchKey = "OUTSIDECUSTOMER",
                TradeType = CustomerClassificationNormalizer.Sales
            });

        dbContext.Items.Add(new Item
        {
            Id = itemId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "Snapshot stock item",
            NameMatchKey = "SNAPSHOTSTOCKITEM",
            SpecificationOriginal = "ORIGINAL-SPEC",
            SpecificationMatchKey = "ORIGINALSPEC",
            ItemKind = ItemKinds.Product,
            TrackingType = ItemTrackingTypes.Stock,
            Unit = "EA",
            CurrentStock = 92m
        });

        dbContext.RentalBillingProfiles.AddRange(
            new RentalBillingProfile
            {
                Id = profileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"PROFILE-{profileId:N}",
                CustomerId = customerId,
                CustomerName = "Original customer",
                MonthlyAmount = 1000m,
                BillingRunsJson = "[]"
            },
            new RentalBillingProfile
            {
                Id = alternateProfileId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                ProfileKey = $"PROFILE-{alternateProfileId:N}",
                CustomerId = alternateCustomerId,
                CustomerName = "Writable attack customer",
                MonthlyAmount = 9999m,
                BillingRunsJson = "[]"
            });

        var invoice = new Invoice
        {
            Id = invoiceId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            InvoiceNumber = "INV-ORIGINAL-001",
            LocalTempNumber = "TMP-ORIGINAL-001",
            TaxInvoiceNumber = "TAX-ORIGINAL-001",
            LinkedRentalBillingProfileId = profileId,
            LinkedRentalBillingRunId = runId,
            VersionGroupId = invoiceId,
            VersionNumber = 7,
            PreviousVersionId = Guid.NewGuid(),
            IsLatestVersion = true,
            VoucherType = VoucherType.Sales,
            SourceWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            InvoiceDate = invoiceDate,
            TotalAmount = 1000m,
            SupplyAmount = 909m,
            VatAmount = 91m,
            VatMode = InvoiceVatModes.Included,
            TaxInvoiceIssued = true,
            PurchaseReceivingRequired = true,
            PurchaseReceivingStatus = InvoiceReceivingStatuses.Confirmed,
            PurchaseReceivedAtUtc = new DateTime(2026, 6, 19, 1, 2, 3, DateTimeKind.Utc),
            PurchaseReceivedByUsername = "original-receiver",
            PurchaseReceivingOfficeCode = OfficeCodeCatalog.Usenet,
            PurchaseReceivingWarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            PurchaseReceivingMemo = "original receiving memo",
            Memo = "original invoice memo"
        };
        invoice.Lines.Add(new InvoiceLine
        {
            Id = lineId,
            InvoiceId = invoiceId,
            ItemId = itemId,
            ItemNameOriginal = "Original line item",
            SpecificationOriginal = "Original line specification",
            Unit = "EA",
            Quantity = 8m,
            UnitPrice = 125m,
            LineAmount = 1000m,
            Remark = "original line remark",
            SerialNumber = "SERIAL-ORIGINAL",
            MaterialNumber = "MATERIAL-ORIGINAL",
            InstallLocation = "original install location",
            RentalStartDate = new DateOnly(2026, 6, 1),
            RentalEndDate = new DateOnly(2027, 5, 31),
            OrderIndex = 4,
            ItemTrackingType = ItemTrackingTypes.Stock
        });
        dbContext.Invoices.Add(invoice);
        dbContext.ItemWarehouseStocks.Add(new ItemWarehouseStock
        {
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            Quantity = 92m
        });
        dbContext.InventoryLedgerEntries.Add(new InventoryLedgerEntry
        {
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ItemId = itemId,
            WarehouseCode = OfficeCodeCatalog.UsenetMainWarehouse,
            SourceType = $"Invoice:{VoucherType.Sales}",
            SourceDocumentId = invoiceId,
            SourceLineId = lineId,
            QuantityDelta = -8m,
            OccurredDate = invoiceDate,
            Note = invoice.InvoiceNumber
        });
        await dbContext.SaveChangesAsync();

        dbContext.ChangeTracker.Clear();
        var persisted = await LoadInvoiceAsync(dbContext, invoiceId);
        return new SeededInvoice(
            invoiceId,
            lineId,
            itemId,
            alternateCustomerId,
            outsideCustomerId,
            alternateProfileId,
            persisted.Revision,
            InvoiceBusinessSnapshot.From(persisted));
    }

    private static SyncPushRequest CreateAttackDeleteRequest(
        SeededInvoice seed,
        Guid customerId,
        string mutationSuffix)
        => new()
        {
            DeviceId = "sync-invoice-delete-test",
            Invoices =
            [
                new InvoiceDto
                {
                    Id = seed.InvoiceId,
                    CustomerId = customerId,
                    CustomerName = "Attacker supplied customer",
                    TenantCode = TenantScopeCatalog.Itworld,
                    OfficeCode = OfficeCodeCatalog.Itworld,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Itworld,
                    InvoiceNumber = "INV-ATTACK",
                    LocalTempNumber = "TMP-ATTACK",
                    TaxInvoiceNumber = "TAX-ATTACK",
                    LinkedRentalBillingProfileId = seed.AlternateProfileId,
                    LinkedRentalBillingRunId = Guid.NewGuid(),
                    VersionGroupId = Guid.NewGuid(),
                    VersionNumber = 99,
                    PreviousVersionId = Guid.NewGuid(),
                    IsLatestVersion = false,
                    VoucherType = VoucherType.Purchase,
                    SourceWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                    InvoiceDate = new DateOnly(2035, 12, 31),
                    TotalAmount = 999999m,
                    SupplyAmount = 888888m,
                    VatAmount = 111111m,
                    VatMode = InvoiceVatModes.None,
                    TaxInvoiceIssued = false,
                    PurchaseReceivingRequired = false,
                    PurchaseReceivingStatus = InvoiceReceivingStatuses.NotRequired,
                    PurchaseReceivedAtUtc = null,
                    PurchaseReceivedByUsername = "attacker",
                    PurchaseReceivingOfficeCode = OfficeCodeCatalog.Itworld,
                    PurchaseReceivingWarehouseCode = OfficeCodeCatalog.ItworldMainWarehouse,
                    PurchaseReceivingMemo = "attack receiving memo",
                    Memo = "attack invoice memo",
                    Lines =
                    [
                        new InvoiceLineDto
                        {
                            Id = Guid.NewGuid(),
                            InvoiceId = seed.InvoiceId,
                            ItemId = seed.ItemId,
                            ItemNameOriginal = "Attack line item",
                            SpecificationOriginal = "Attack specification",
                            Unit = "BOX",
                            Quantity = 999m,
                            UnitPrice = 777m,
                            LineAmount = 776223m,
                            Remark = "attack line",
                            SerialNumber = "ATTACK-SERIAL",
                            MaterialNumber = "ATTACK-MATERIAL",
                            InstallLocation = "attack location",
                            OrderIndex = 99,
                            ItemTrackingType = ItemTrackingTypes.Stock
                        }
                    ],
                    IsDeleted = true,
                    Revision = seed.Revision,
                    ExpectedRevision = seed.Revision,
                    UpdatedAtUtc = new DateTime(2035, 12, 31, 23, 59, 59, DateTimeKind.Utc),
                    MutationId = $"{mutationSuffix}:{seed.InvoiceId:N}",
                    MutationCreatedAtUtc = new DateTime(2026, 7, 26, 12, 0, 0, DateTimeKind.Utc)
                }
            ]
        };

    private AppDbContext CreateDbContext(TestCurrentUserContext currentUser)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_databasePath};Cache=Shared;Pooling=False")
            .Options;
        return new AppDbContext(options, currentUser, new RevisionClock());
    }

    private static SyncController CreateSyncController(
        AppDbContext dbContext,
        TestCurrentUserContext currentUser)
    {
        var revisionClock = new RevisionClock();
        return new SyncController(
            dbContext,
            currentUser,
            new StubInvoiceNumberService(),
            new OfficeScopeService(currentUser, dbContext),
            new StubCentralFileStorage(),
            revisionClock,
            new InventoryLedgerService(dbContext),
            new InvoiceStockSnapshotService(dbContext, revisionClock),
            new RentalAssignmentHistoryService(dbContext),
            new RentalSettlementRecalculationService(dbContext));
    }

    private static TestCurrentUserContext CreateAdminUser()
        => new()
        {
            Username = "admin",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            IsAdmin = true
        };

    private static TestCurrentUserContext CreateOfficeInvoiceEditor()
        => new()
        {
            Username = "invoice-editor",
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = [PermissionNames.InvoiceEdit]
        };

    private static SyncPushResult AssertOk(ActionResult<SyncPushResult> response)
    {
        var ok = Assert.IsType<OkObjectResult>(response.Result);
        return Assert.IsType<SyncPushResult>(ok.Value);
    }

    private static async Task<Invoice> LoadInvoiceAsync(AppDbContext dbContext, Guid invoiceId)
        => await dbContext.Invoices
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Include(invoice => invoice.Lines)
            .SingleAsync(invoice => invoice.Id == invoiceId);

    private static Task<decimal> LoadWarehouseQuantityAsync(AppDbContext dbContext, Guid itemId)
        => dbContext.ItemWarehouseStocks.AsNoTracking()
            .Where(stock =>
                stock.ItemId == itemId &&
                stock.WarehouseCode == OfficeCodeCatalog.UsenetMainWarehouse)
            .Select(stock => stock.Quantity)
            .SingleAsync();

    private static Task<decimal> LoadItemCurrentStockAsync(AppDbContext dbContext, Guid itemId)
        => dbContext.Items.IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.Id == itemId)
            .Select(item => item.CurrentStock)
            .SingleAsync();

    private static void AssertBusinessSnapshot(InvoiceBusinessSnapshot expected, Invoice actual)
    {
        Assert.Equal(expected.CustomerId, actual.CustomerId);
        Assert.Equal(expected.TenantCode, actual.TenantCode);
        Assert.Equal(expected.OfficeCode, actual.OfficeCode);
        Assert.Equal(expected.ResponsibleOfficeCode, actual.ResponsibleOfficeCode);
        Assert.Equal(expected.InvoiceNumber, actual.InvoiceNumber);
        Assert.Equal(expected.LocalTempNumber, actual.LocalTempNumber);
        Assert.Equal(expected.TaxInvoiceNumber, actual.TaxInvoiceNumber);
        Assert.Equal(expected.LinkedRentalBillingProfileId, actual.LinkedRentalBillingProfileId);
        Assert.Equal(expected.LinkedRentalBillingRunId, actual.LinkedRentalBillingRunId);
        Assert.Equal(expected.VersionGroupId, actual.VersionGroupId);
        Assert.Equal(expected.VersionNumber, actual.VersionNumber);
        Assert.Equal(expected.PreviousVersionId, actual.PreviousVersionId);
        Assert.Equal(expected.VoucherType, actual.VoucherType);
        Assert.Equal(expected.SourceWarehouseCode, actual.SourceWarehouseCode);
        Assert.Equal(expected.InvoiceDate, actual.InvoiceDate);
        Assert.Equal(expected.TotalAmount, actual.TotalAmount);
        Assert.Equal(expected.SupplyAmount, actual.SupplyAmount);
        Assert.Equal(expected.VatAmount, actual.VatAmount);
        Assert.Equal(expected.VatMode, actual.VatMode);
        Assert.Equal(expected.TaxInvoiceIssued, actual.TaxInvoiceIssued);
        Assert.Equal(expected.PurchaseReceivingRequired, actual.PurchaseReceivingRequired);
        Assert.Equal(expected.PurchaseReceivingStatus, actual.PurchaseReceivingStatus);
        Assert.Equal(expected.PurchaseReceivedAtUtc, actual.PurchaseReceivedAtUtc);
        Assert.Equal(expected.PurchaseReceivedByUsername, actual.PurchaseReceivedByUsername);
        Assert.Equal(expected.PurchaseReceivingOfficeCode, actual.PurchaseReceivingOfficeCode);
        Assert.Equal(expected.PurchaseReceivingWarehouseCode, actual.PurchaseReceivingWarehouseCode);
        Assert.Equal(expected.PurchaseReceivingMemo, actual.PurchaseReceivingMemo);
        Assert.Equal(expected.Memo, actual.Memo);

        var actualLines = actual.Lines.OrderBy(line => line.Id).ToList();
        Assert.Equal(expected.Lines.Count, actualLines.Count);
        for (var index = 0; index < expected.Lines.Count; index++)
        {
            var expectedLine = expected.Lines[index];
            var actualLine = actualLines[index];
            Assert.Equal(expectedLine.Id, actualLine.Id);
            Assert.Equal(expectedLine.InvoiceId, actualLine.InvoiceId);
            Assert.Equal(expectedLine.ItemId, actualLine.ItemId);
            Assert.Equal(expectedLine.ItemNameOriginal, actualLine.ItemNameOriginal);
            Assert.Equal(expectedLine.SpecificationOriginal, actualLine.SpecificationOriginal);
            Assert.Equal(expectedLine.Unit, actualLine.Unit);
            Assert.Equal(expectedLine.Quantity, actualLine.Quantity);
            Assert.Equal(expectedLine.UnitPrice, actualLine.UnitPrice);
            Assert.Equal(expectedLine.LineAmount, actualLine.LineAmount);
            Assert.Equal(expectedLine.Remark, actualLine.Remark);
            Assert.Equal(expectedLine.SerialNumber, actualLine.SerialNumber);
            Assert.Equal(expectedLine.MaterialNumber, actualLine.MaterialNumber);
            Assert.Equal(expectedLine.InstallLocation, actualLine.InstallLocation);
            Assert.Equal(expectedLine.RentalStartDate, actualLine.RentalStartDate);
            Assert.Equal(expectedLine.RentalEndDate, actualLine.RentalEndDate);
            Assert.Equal(expectedLine.OrderIndex, actualLine.OrderIndex);
            Assert.Equal(expectedLine.ItemTrackingType, actualLine.ItemTrackingType);
        }
    }

    private sealed record SeededInvoice(
        Guid InvoiceId,
        Guid LineId,
        Guid ItemId,
        Guid AlternateCustomerId,
        Guid OutsideCustomerId,
        Guid AlternateProfileId,
        long Revision,
        InvoiceBusinessSnapshot Snapshot);

    private sealed record InvoiceBusinessSnapshot(
        Guid CustomerId,
        string TenantCode,
        string OfficeCode,
        string ResponsibleOfficeCode,
        string InvoiceNumber,
        string LocalTempNumber,
        string TaxInvoiceNumber,
        Guid? LinkedRentalBillingProfileId,
        Guid? LinkedRentalBillingRunId,
        Guid VersionGroupId,
        int VersionNumber,
        Guid? PreviousVersionId,
        bool IsLatestVersion,
        VoucherType VoucherType,
        string SourceWarehouseCode,
        DateOnly InvoiceDate,
        decimal TotalAmount,
        decimal SupplyAmount,
        decimal VatAmount,
        string VatMode,
        bool TaxInvoiceIssued,
        bool PurchaseReceivingRequired,
        string PurchaseReceivingStatus,
        DateTime? PurchaseReceivedAtUtc,
        string PurchaseReceivedByUsername,
        string PurchaseReceivingOfficeCode,
        string PurchaseReceivingWarehouseCode,
        string PurchaseReceivingMemo,
        string Memo,
        IReadOnlyList<InvoiceLineBusinessSnapshot> Lines)
    {
        public static InvoiceBusinessSnapshot From(Invoice invoice)
            => new(
                invoice.CustomerId,
                invoice.TenantCode,
                invoice.OfficeCode,
                invoice.ResponsibleOfficeCode,
                invoice.InvoiceNumber,
                invoice.LocalTempNumber,
                invoice.TaxInvoiceNumber,
                invoice.LinkedRentalBillingProfileId,
                invoice.LinkedRentalBillingRunId,
                invoice.VersionGroupId,
                invoice.VersionNumber,
                invoice.PreviousVersionId,
                invoice.IsLatestVersion,
                invoice.VoucherType,
                invoice.SourceWarehouseCode,
                invoice.InvoiceDate,
                invoice.TotalAmount,
                invoice.SupplyAmount,
                invoice.VatAmount,
                invoice.VatMode,
                invoice.TaxInvoiceIssued,
                invoice.PurchaseReceivingRequired,
                invoice.PurchaseReceivingStatus,
                invoice.PurchaseReceivedAtUtc,
                invoice.PurchaseReceivedByUsername,
                invoice.PurchaseReceivingOfficeCode,
                invoice.PurchaseReceivingWarehouseCode,
                invoice.PurchaseReceivingMemo,
                invoice.Memo,
                invoice.Lines
                    .OrderBy(line => line.Id)
                    .Select(InvoiceLineBusinessSnapshot.From)
                    .ToList());
    }

    private sealed record InvoiceLineBusinessSnapshot(
        Guid Id,
        Guid InvoiceId,
        Guid? ItemId,
        string ItemNameOriginal,
        string SpecificationOriginal,
        string Unit,
        decimal Quantity,
        decimal UnitPrice,
        decimal LineAmount,
        string Remark,
        string SerialNumber,
        string MaterialNumber,
        string InstallLocation,
        DateOnly? RentalStartDate,
        DateOnly? RentalEndDate,
        int OrderIndex,
        string ItemTrackingType)
    {
        public static InvoiceLineBusinessSnapshot From(InvoiceLine line)
            => new(
                line.Id,
                line.InvoiceId,
                line.ItemId,
                line.ItemNameOriginal,
                line.SpecificationOriginal,
                line.Unit,
                line.Quantity,
                line.UnitPrice,
                line.LineAmount,
                line.Remark,
                line.SerialNumber,
                line.MaterialNumber,
                line.InstallLocation,
                line.RentalStartDate,
                line.RentalEndDate,
                line.OrderIndex,
                line.ItemTrackingType);
    }

    private sealed class TestCurrentUserContext : ICurrentUserContext
    {
        public Guid? UserId { get; init; } = Guid.NewGuid();
        public string Username { get; init; } = string.Empty;
        public string TenantCode { get; init; } = TenantScopeCatalog.UsenetGroup;
        public string OfficeCode { get; init; } = OfficeCodeCatalog.Usenet;
        public string ScopeType { get; init; } = TenantScopeCatalog.ScopeOfficeOnly;
        public bool IsAdmin { get; init; }
        public bool IsGodMode { get; init; }
        public IReadOnlyCollection<string> Permissions { get; init; } = Array.Empty<string>();

        public bool HasPermission(string permission)
            => IsAdmin ||
               IsGodMode ||
               Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);
    }

    private sealed class StubInvoiceNumberService : IInvoiceNumberService
    {
        public Task<string> GenerateAsync(
            Guid customerId,
            DateOnly invoiceDate,
            CancellationToken cancellationToken = default)
            => Task.FromResult($"INV-{invoiceDate:yyyyMMdd}-{customerId:N}");
    }

    private sealed class StubCentralFileStorage : ICentralFileStorage
    {
        public string RootPath => Path.GetTempPath();

        public Task<string> SaveBytesAsync(
            string area,
            string ownerId,
            Guid fileId,
            string fileName,
            byte[] content,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(RootPath, fileName));

        public byte[] ReadBytes(string? storedPath, byte[]? fallback = null)
            => fallback ?? [];

        public void DeleteIfExists(string? storedPath)
        {
        }
    }
}
