using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TransactionAttachmentFileConsistencyTests
{
    [Fact]
    public async Task ApplyServerPurgeTransaction_KeepsAttachmentFileWhenLocalDbCommitFails()
    {
        PrepareAppRoot("georaeplan-server-purge-attachment-commit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-server-purge-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(attachmentFile, "server purge attachment evidence");

            db.Customers.Add(CreateCustomer(customerId, "Server purge attachment customer"));
            db.Transactions.Add(CreateDeletedTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(CreateAttachment(attachmentId, transactionId, attachmentFile, isDeleted: true));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_transaction_delete
                BEFORE DELETE ON Transactions
                BEGIN
                    SELECT RAISE(ABORT, 'blocked transaction delete');
                END;
                """);

            var session = CreateAdminSession();
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                local.ApplyServerPurgeRecycleBinEntryAsync(RecycleBinEntityKind.Transaction, transactionId));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(attachmentFile));
            Assert.True(await db.Transactions.IgnoreQueryFilters().AnyAsync(current => current.Id == transactionId));
            Assert.True(await db.TransactionAttachments.IgnoreQueryFilters().AnyAsync(current => current.Id == attachmentId));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullDeletedTransactionAttachment_KeepsExistingFileWhenLocalDbCommitFails()
    {
        PrepareAppRoot("georaeplan-sync-pull-deleted-attachment-commit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-delete-{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(attachmentFile, "sync delete attachment evidence");

            db.Customers.Add(CreateCustomer(customerId, "Sync deleted attachment customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(CreateAttachment(attachmentId, transactionId, attachmentFile, isDeleted: false));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_attachment_update
                BEFORE UPDATE ON TransactionAttachments
                BEGIN
                    SELECT RAISE(ABORT, 'blocked attachment update');
                END;
                """);

            using var sync = CreateSyncService(db);
            var now = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "ApplyPullAsync",
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 200,
                        TransactionAttachments =
                        {
                            new TransactionAttachmentDto
                            {
                                Id = attachmentId,
                                TransactionId = transactionId,
                                FileName = Path.GetFileName(attachmentFile),
                                UploadedAtUtc = now,
                                CreatedAtUtc = now.AddMinutes(-1),
                                UpdatedAtUtc = now,
                                Revision = 200,
                                IsDeleted = true
                            }
                        }
                    },
                    0L,
                    CancellationToken.None,
                    false));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(attachmentFile));
            var stored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.False(stored.IsDeleted);
            Assert.Equal(attachmentFile, stored.StoredPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task SyncPullRenamedTransactionAttachment_KeepsOldFileWhenLocalDbCommitFails()
    {
        PrepareAppRoot("georaeplan-sync-pull-renamed-attachment-commit-failure");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var customerId = Guid.NewGuid();
            var transactionId = Guid.NewGuid();
            var attachmentId = Guid.NewGuid();
            var attachmentFile = Path.Combine(Path.GetTempPath(), $"georaeplan-sync-rename-{Guid.NewGuid():N}-old.txt");
            await File.WriteAllTextAsync(attachmentFile, "sync rename old attachment evidence");

            db.Customers.Add(CreateCustomer(customerId, "Sync renamed attachment customer"));
            db.Transactions.Add(CreateActiveTransaction(transactionId, customerId));
            db.TransactionAttachments.Add(CreateAttachment(attachmentId, transactionId, attachmentFile, isDeleted: false));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_attachment_rename_update
                BEFORE UPDATE ON TransactionAttachments
                BEGIN
                    SELECT RAISE(ABORT, 'blocked attachment rename update');
                END;
                """);

            using var sync = CreateSyncService(db);
            var now = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<Exception>(() =>
                InvokePrivateInstanceTaskAsync(
                    sync,
                    "ApplyPullAsync",
                    new SyncPullResponse
                    {
                        CurrentServerRevision = 201,
                        TransactionAttachments =
                        {
                            new TransactionAttachmentDto
                            {
                                Id = attachmentId,
                                TransactionId = transactionId,
                                FileName = $"renamed-{attachmentId:N}.txt",
                                FileContent = "sync rename new attachment evidence"u8.ToArray(),
                                UploadedAtUtc = now,
                                CreatedAtUtc = now.AddMinutes(-1),
                                UpdatedAtUtc = now,
                                Revision = 201,
                                IsDeleted = false
                            }
                        }
                    },
                    0L,
                    CancellationToken.None,
                    false));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(attachmentFile));
            var stored = await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AsNoTracking()
                .SingleAsync(current => current.Id == attachmentId);
            Assert.False(stored.IsDeleted);
            Assert.Equal(attachmentFile, stored.StoredPath);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static SyncService CreateSyncService(LocalDbContext db)
    {
        var session = CreateAdminSession();
        var dispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), dispatcher, session);
        var rental = new RentalStateService(db, local);
        var diagnostics = new SyncDiagnosticsService(session);
        var api = new ErpApiClient(new HttpClient { BaseAddress = new Uri("http://localhost/") }, session);
        return new SyncService(db, local, rental, api, session, dispatcher, diagnostics);
    }

    private static LocalCustomer CreateCustomer(Guid customerId, string customerName)
        => new()
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = customerName,
            NameMatchKey = customerName,
            TradeType = CustomerTradeTypes.Sales,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            IsDeleted = false,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalTransaction CreateActiveTransaction(Guid transactionId, Guid customerId)
        => CreateTransaction(transactionId, customerId, isDeleted: false);

    private static LocalTransaction CreateDeletedTransaction(Guid transactionId, Guid customerId)
        => CreateTransaction(transactionId, customerId, isDeleted: true);

    private static LocalTransaction CreateTransaction(Guid transactionId, Guid customerId, bool isDeleted)
        => new()
        {
            Id = transactionId,
            CustomerId = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = new DateOnly(2026, 6, 18),
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            ReceiptTotal = 1000m,
            SettlementAmount = 1000m,
            IsDeleted = isDeleted,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static LocalTransactionAttachment CreateAttachment(Guid attachmentId, Guid transactionId, string filePath, bool isDeleted)
        => new()
        {
            Id = attachmentId,
            TransactionId = transactionId,
            FileName = Path.GetFileName(filePath),
            StoredFileName = Path.GetFileName(filePath),
            StoredPath = filePath,
            FileSize = new FileInfo(filePath).Length,
            UploadedAtUtc = DateTime.UtcNow,
            IsDeleted = isDeleted,
            IsDirty = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }

    private static async Task InvokePrivateInstanceTaskAsync(object target, string methodName, params object?[]? args)
    {
        var method = target.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);

        var result = method!.Invoke(target, args);
        Assert.NotNull(result);
        var task = Assert.IsAssignableFrom<Task>(result);
        await task;
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin
        });
        return session;
    }
}
