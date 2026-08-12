using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class BusinessDatabaseAtomicCacheReplacementTests
{
    [Fact]
    public async Task ResetWithinTransaction_RollbackRestoresPreviousRowsAndSettings()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();
        var oldCustomer = CreateCustomer("old-customer");
        db.Customers.Add(oldCustomer);
        db.Settings.Add(new LocalSetting
        {
            Key = "InvoiceFilter.From",
            Value = "2026-08-01"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = CreateLocalStateService(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);

        await service.ResetBusinessDataCacheWithAttachmentJournalAsync(
            attachmentFiles);

        Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync());
        Assert.False(await db.Settings.AnyAsync(setting =>
            setting.Key == "InvoiceFilter.From"));

        await transaction.RollbackAsync();
        attachmentFiles.Rollback();
        db.ChangeTracker.Clear();

        Assert.True(await db.Customers.IgnoreQueryFilters().AnyAsync(customer =>
            customer.Id == oldCustomer.Id));
        Assert.Equal(
            "2026-08-01",
            (await db.Settings.SingleAsync(setting =>
                setting.Key == "InvoiceFilter.From")).Value);
    }

    [Fact]
    public async Task ResetAndApplyWithinTransaction_CommitsOnlyReplacementCache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();
        var oldCustomer = CreateCustomer("old-customer");
        var targetCustomer = CreateCustomer("target-customer");
        db.Customers.Add(oldCustomer);
        db.Settings.Add(new LocalSetting
        {
            Key = "InvoiceFilter.From",
            Value = "2026-08-01"
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = CreateLocalStateService(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);

        await service.ResetBusinessDataCacheWithAttachmentJournalAsync(
            attachmentFiles);
        db.Customers.Add(targetCustomer);
        db.Settings.Add(new LocalSetting
        {
            Key = "LastSyncRevision",
            Value = "42"
        });
        await db.SaveChangesAsync();
        await attachmentFiles.StageCommitEvidenceAsync(db, CancellationToken.None);
        attachmentFiles.Promote();
        await transaction.CommitAsync();
        await transaction.DisposeAsync();
        await attachmentFiles.CompleteAfterDatabaseCommitAsync(
            db,
            CancellationToken.None);
        db.ChangeTracker.Clear();

        Assert.False(await db.Customers.IgnoreQueryFilters().AnyAsync(customer =>
            customer.Id == oldCustomer.Id));
        Assert.True(await db.Customers.IgnoreQueryFilters().AnyAsync(customer =>
            customer.Id == targetCustomer.Id));
        Assert.False(await db.Settings.AnyAsync(setting =>
            setting.Key == "InvoiceFilter.From"));
        Assert.Equal(
            "42",
            (await db.Settings.SingleAsync(setting =>
                setting.Key == "LastSyncRevision")).Value);
    }

    [Fact]
    public async Task ResetWithoutTransaction_IsRejectedBeforeDeletingRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDbContext(connection);
        await db.Database.EnsureCreatedAsync();
        var oldCustomer = CreateCustomer("old-customer");
        db.Customers.Add(oldCustomer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = CreateLocalStateService(db);
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ResetBusinessDataCacheWithAttachmentJournalAsync(
                attachmentFiles));

        Assert.True(await db.Customers.IgnoreQueryFilters().AnyAsync(customer =>
            customer.Id == oldCustomer.Id));
    }

    private static LocalDbContext CreateDbContext(SqliteConnection connection)
        => new(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);

    private static LocalStateService CreateLocalStateService(LocalDbContext db)
        => new(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            new SessionState());

    private static LocalCustomer CreateCustomer(string name)
        => new()
        {
            Id = Guid.NewGuid(),
            NameOriginal = name,
            NameMatchKey = name.ToUpperInvariant(),
            TenantCode = "USENET",
            OfficeCode = "USENET",
            ResponsibleOfficeCode = "USENET",
            TradeType = "일반",
            IsDirty = false
        };
}
