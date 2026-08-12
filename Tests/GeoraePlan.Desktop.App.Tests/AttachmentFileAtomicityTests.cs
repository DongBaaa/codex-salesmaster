using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Diagnostics;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class AttachmentFileAtomicityTests
{
    [Fact]
    public void AppStartupAndEmptyPull_WireDurableAttachmentJournalRecovery()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "App.xaml.cs"));
        var syncSource = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "Desktop",
                "거래플랜.Desktop.App",
                "Services",
                "SyncService.cs"));

        Assert.Contains(
            "await AttachmentFileJournal.RecoverIncompleteJournalsAsync(",
            appSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "await LocalDbInitializer.InitializeAsync(db);",
            appSource,
            StringComparison.Ordinal);
        Assert.True(
            appSource.IndexOf(
                "await LocalDbInitializer.InitializeAsync(db);",
                StringComparison.Ordinal) <
            appSource.IndexOf(
                "await AttachmentFileJournal.RecoverIncompleteJournalsAsync(",
                StringComparison.Ordinal));

        var recoveryIndex = syncSource.IndexOf(
            "await RecoverIncompleteAttachmentFileJournalsAsync(ct);",
            StringComparison.Ordinal);
        var emptyPullReturnIndex = syncSource.IndexOf(
            "if (dtos.Count == 0)",
            recoveryIndex,
            StringComparison.Ordinal);
        Assert.True(recoveryIndex >= 0);
        Assert.True(emptyPullReturnIndex > recoveryIndex);
    }

    [Fact]
    public void ProductionAttachmentJournals_UsePrivateRootOnAttachmentVolume()
    {
        var repositoryRoot = FindRepositoryRoot();
        var appPathsSource = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "Desktop",
            "거래플랜.Desktop.App",
            "Infrastructure",
            "AppPaths.cs"));
        Assert.Contains(
            "AttachmentFileJournalDir { get; } = Path.Combine(AttachmentsDir, \".file-journals\")",
            appPathsSource,
            StringComparison.Ordinal);

        var productionSources = new[]
        {
            Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App", "App.xaml.cs"),
            Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App", "Services", "LocalStateService.cs"),
            Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App", "Services", "LocalStateService.BusinessDatabase.cs"),
            Path.Combine(repositoryRoot, "Desktop", "거래플랜.Desktop.App", "Services", "SyncService.cs")
        };
        foreach (var sourcePath in productionSources)
        {
            var source = File.ReadAllText(sourcePath);
            Assert.Contains("AppPaths.AttachmentFileJournalDir", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "Path.Combine(AppPaths.TempDir, \"attachment-file-journals\")",
                source,
                StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Constructor_RejectsJournalRootOnDifferentFilesystemVolume()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new AttachmentFileJournal(
                @"C:\georaeplan-journal-must-not-be-created",
                @"D:\georaeplan-attachments-must-not-be-created"));

        Assert.Contains("same filesystem volume", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveTransactionAttachment_DatabaseSaveFailure_LeavesNoFinalFileOrRow()
    {
        using var scope = new TemporaryDirectory();
        var sourcePath = Path.Combine(scope.Path, "receipt.pdf");
        await File.WriteAllBytesAsync(sourcePath, "%PDF-1.4\nattachment-test"u8.ToArray());

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .Options;
        var transactionId = Guid.NewGuid();
        await using (var setupDb = new LocalDbContext(baseOptions))
        {
            await setupDb.Database.EnsureCreatedAsync();
            setupDb.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                IsDirty = false
            });
            await setupDb.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection)
            .AddInterceptors(new ThrowOnSaveChangesInterceptor())
            .Options;
        await using var db = new LocalDbContext(failingOptions);
        var session = CreateAdminSession();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            session);
        var attachmentDirectory = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            transactionId.ToString("N"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SaveTransactionAttachmentAsync(
                transactionId,
                sourcePath,
                "입금확인증",
                "must roll back",
                session));

        Assert.False(Directory.Exists(attachmentDirectory));
        Assert.False(await db.TransactionAttachments
            .IgnoreQueryFilters()
            .AnyAsync(current => current.TransactionId == transactionId));
    }

    [Fact]
    public async Task SaveTransactionAttachment_CommitCompletedThenThrew_ReturnsCommittedSuccess()
    {
        using var scope = new TemporaryDirectory();
        var sourcePath = Path.Combine(scope.Path, "commit-ambiguous.pdf");
        var databasePath = Path.Combine(scope.Path, "commit-ambiguous.db");
        await File.WriteAllBytesAsync(
            sourcePath,
            "%PDF-1.4\ncommit-ambiguous"u8.ToArray());
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var transactionId = Guid.NewGuid();
        await using (var setupDb = new LocalDbContext(baseOptions))
        {
            await setupDb.Database.EnsureCreatedAsync();
            setupDb.Transactions.Add(new LocalTransaction
            {
                Id = transactionId,
                CustomerId = Guid.NewGuid(),
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                ReceiptTotal = 100m,
                IsDirty = false
            });
            await setupDb.SaveChangesAsync();
        }

        var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ThrowAfterCommitInterceptor())
            .Options;
        await using var db = new LocalDbContext(ambiguousOptions);
        var session = CreateAdminSession();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            session);

        var result = await service.SaveTransactionAttachmentAsync(
            transactionId,
            sourcePath,
            "입금확인증",
            "commit ambiguity",
            session);

        await using var verificationDb = new LocalDbContext(baseOptions);
        var attachment = await verificationDb.TransactionAttachments
            .IgnoreQueryFilters()
            .AsNoTracking()
            .SingleAsync(current => current.TransactionId == transactionId);
        Assert.True(result.Success);
        Assert.Equal(attachment.Id, result.EntityId);
        Assert.True(File.Exists(attachment.StoredPath));
        Assert.Equal(
            "%PDF-1.4\ncommit-ambiguous",
            await File.ReadAllTextAsync(attachment.StoredPath));
        File.Delete(attachment.StoredPath);
        var attachmentDirectory = Path.GetDirectoryName(attachment.StoredPath);
        if (!string.IsNullOrWhiteSpace(attachmentDirectory) &&
            Directory.Exists(attachmentDirectory) &&
            !Directory.EnumerateFileSystemEntries(attachmentDirectory).Any())
        {
            Directory.Delete(attachmentDirectory);
        }
    }

    [Fact]
    public async Task StagedWrite_RollbackAfterDatabaseFailure_RemovesNewFinalFile()
    {
        using var scope = new TemporaryDirectory();
        var destinationPath = Path.Combine(scope.Path, "attachments", "receipt.pdf");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        await journal.StageWriteAsync(destinationPath, "%PDF-test"u8.ToArray());

        Assert.False(File.Exists(destinationPath));

        journal.Promote();
        Assert.True(File.Exists(destinationPath));

        journal.Rollback();

        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task StagedWrite_DifferentExistingFile_IsRejectedAndPreserved()
    {
        using var scope = new TemporaryDirectory();
        var destinationPath = Path.Combine(scope.Path, "attachments", "receipt.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(destinationPath, "previous");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.StageWriteAsync(destinationPath, "replacement"u8.ToArray()));

        Assert.Equal("previous", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task StagedDeleteThenIdenticalWrite_SamePathKeepsCommittedFile()
    {
        using var scope = new TemporaryDirectory();
        var destinationPath = Path.Combine(scope.Path, "attachments", "receipt.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(destinationPath, "previous");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        journal.StageDelete(destinationPath);
        await journal.StageWriteAsync(destinationPath, "previous"u8.ToArray());
        journal.Promote();
        journal.Complete();

        Assert.Equal("previous", await File.ReadAllTextAsync(destinationPath));
    }

    [Fact]
    public async Task StagedWriteThenDeleteBeforePromote_CancelsPendingWrite()
    {
        using var scope = new TemporaryDirectory();
        var destinationPath = Path.Combine(scope.Path, "attachments", "receipt.pdf");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        await journal.StageWriteAsync(
            destinationPath,
            "must-not-survive"u8.ToArray());
        journal.StageDelete(destinationPath);
        journal.Promote();
        journal.Complete();

        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task CompleteAfterDatabaseCommit_UnreferencedPromotedWrite_RemovesOrphan()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var destinationPath = Path.Combine(attachmentRoot, "orphan.pdf");
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-unreferenced"u8.ToArray());
        journal.Promote();

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        await journal.CompleteAfterDatabaseCommitAsync(db);

        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task MutationAfterPromote_IsRejectedAndCannotEscapeDurableManifest()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var firstPath = Path.Combine(attachmentRoot, "first.pdf");
        var latePath = Path.Combine(attachmentRoot, "late.pdf");
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        await journal.StageWriteAsync(firstPath, "%PDF-first"u8.ToArray());
        journal.Promote();

        Assert.Throws<InvalidOperationException>(() =>
            journal.StageDelete(firstPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.StageWriteAsync(latePath, "%PDF-late"u8.ToArray()));

        journal.Rollback();
        Assert.False(File.Exists(firstPath));
        Assert.False(File.Exists(latePath));
    }

    [Fact]
    public async Task Promote_ParentReplacedWithJunction_RejectsOutsideWrite()
    {
        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var destinationDirectory = Path.Combine(attachmentRoot, "swapped");
        var destinationPath = Path.Combine(destinationDirectory, "receipt.pdf");
        var escapedPath = Path.Combine(outsideScope.Path, "receipt.pdf");
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-must-not-escape"u8.ToArray());

        CreateDirectoryJunction(destinationDirectory, outsideScope.Path);
        try
        {
            Assert.Throws<InvalidOperationException>(() => journal.Promote());
            Assert.False(File.Exists(escapedPath));
        }
        finally
        {
            DeleteDirectoryLink(destinationDirectory);
        }
    }

    [Fact]
    public async Task Promote_ConcurrentParentJunctionSwap_IsBlockedByMutationLease()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var destinationDirectory = Path.Combine(attachmentRoot, "stable-parent");
        var movedDirectory = Path.Combine(scope.Path, "moved-parent");
        var destinationPath = Path.Combine(destinationDirectory, "receipt.pdf");
        var escapedPath = Path.Combine(outsideScope.Path, "receipt.pdf");
        Directory.CreateDirectory(destinationDirectory);
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-parent-lease-write"u8.ToArray());
        var swapBlocked = false;
        var swapCompleted = false;

        AttachmentFileJournal.BeforePathMutationForTesting = path =>
        {
            if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Move(destinationDirectory, movedDirectory);
                CreateDirectoryJunction(destinationDirectory, outsideScope.Path);
                swapCompleted = true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                swapBlocked = true;
                if (Directory.Exists(movedDirectory) &&
                    !Directory.Exists(destinationDirectory))
                {
                    Directory.Move(movedDirectory, destinationDirectory);
                }
            }
        };

        Exception? promoteFailure = null;
        try
        {
            journal.Promote();
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            promoteFailure = ex;
        }
        finally
        {
            AttachmentFileJournal.BeforePathMutationForTesting = null;
            if (swapCompleted)
                DeleteDirectoryLink(destinationDirectory);
        }

        Assert.True(
            swapBlocked || (swapCompleted && promoteFailure is not null));
        if (swapBlocked)
        {
            Assert.Equal(
                "%PDF-parent-lease-write",
                await File.ReadAllTextAsync(destinationPath));
        }
        else
        {
            Assert.False(File.Exists(
                Path.Combine(movedDirectory, "receipt.pdf")));
        }
        Assert.False(File.Exists(escapedPath));
        journal.Rollback();
    }

    [Fact]
    public async Task Promote_MissingParentJunctionInsertedDuringCreation_DoesNotTraverseOutside()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var missingParent = Path.Combine(attachmentRoot, "missing-parent");
        var destinationDirectory = Path.Combine(missingParent, "nested");
        var destinationPath = Path.Combine(destinationDirectory, "receipt.pdf");
        var escapedDirectory = Path.Combine(outsideScope.Path, "nested");
        var escapedPath = Path.Combine(escapedDirectory, "receipt.pdf");
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-missing-parent-race"u8.ToArray());

        AttachmentFileJournal.BeforeDirectoryCreateForTesting = path =>
        {
            if (string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(missingParent),
                    StringComparison.OrdinalIgnoreCase))
            {
                CreateDirectoryJunction(missingParent, outsideScope.Path);
            }
        };

        try
        {
            Assert.Throws<InvalidOperationException>(() => journal.Promote());
            Assert.False(Directory.Exists(escapedDirectory));
            Assert.False(File.Exists(escapedPath));
        }
        finally
        {
            AttachmentFileJournal.BeforeDirectoryCreateForTesting = null;
            DeleteDirectoryLink(missingParent);
        }
    }

    [Fact]
    public async Task Promote_RecoveryManifestTempLeafReplacedBeforeMove_IsRejected()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        var destinationPath = Path.Combine(attachmentRoot, "receipt.pdf");
        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-manifest-leaf-identity"u8.ToArray());
        string? displacedTemporaryPath = null;

        AttachmentFileJournal.BeforeManifestMoveForTesting = temporaryPath =>
        {
            if (!Path.GetFullPath(temporaryPath).StartsWith(
                    $"{Path.GetFullPath(journalRoot).TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)}{Path.DirectorySeparatorChar}",
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            displacedTemporaryPath = $"{temporaryPath}.displaced";
            File.Move(temporaryPath, displacedTemporaryPath);
            File.WriteAllText(
                temporaryPath,
                "{\"Version\":2,\"Writes\":[],\"Deletes\":[]}");
        };

        try
        {
            Assert.Throws<IOException>(() => journal.Promote());
        }
        finally
        {
            AttachmentFileJournal.BeforeManifestMoveForTesting = null;
        }

        Assert.False(File.Exists(destinationPath));
        Assert.NotNull(displacedTemporaryPath);
        Assert.True(File.Exists(displacedTemporaryPath));
        Assert.Single(Directory.EnumerateDirectories(
            journalRoot,
            "attachment-files-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Complete_ParentReplacedWithJunction_PreservesOutsideFile()
    {
        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var destinationDirectory = Path.Combine(attachmentRoot, "swapped");
        var destinationPath = Path.Combine(destinationDirectory, "receipt.pdf");
        var movedDirectory = Path.Combine(outsideScope.Path, "moved");
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(destinationPath, "%PDF-must-stay");
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        journal.StageDelete(destinationPath);
        journal.Promote();

        Directory.Move(destinationDirectory, movedDirectory);
        CreateDirectoryJunction(destinationDirectory, movedDirectory);
        try
        {
            journal.Complete();

            Assert.Equal(
                "%PDF-must-stay",
                await File.ReadAllTextAsync(
                    Path.Combine(movedDirectory, "receipt.pdf")));
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(attachmentRoot, ".file-journals"),
                "attachment-files-*",
                SearchOption.TopDirectoryOnly));
        }
        finally
        {
            DeleteDirectoryLink(destinationDirectory);
        }
    }

    [Fact]
    public async Task Complete_ConcurrentParentJunctionSwap_IsBlockedByMutationLease()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var destinationDirectory = Path.Combine(attachmentRoot, "stable-parent");
        var movedDirectory = Path.Combine(scope.Path, "moved-parent");
        var destinationPath = Path.Combine(destinationDirectory, "receipt.pdf");
        var outsidePath = Path.Combine(outsideScope.Path, "receipt.pdf");
        Directory.CreateDirectory(destinationDirectory);
        await File.WriteAllTextAsync(destinationPath, "%PDF-delete-target");
        await File.WriteAllTextAsync(outsidePath, "%PDF-outside-sentinel");
        using var journal = new AttachmentFileJournal(
            Path.Combine(attachmentRoot, ".file-journals"),
            attachmentRoot);
        journal.StageDelete(destinationPath);
        journal.Promote();
        var swapBlocked = false;
        var swapCompleted = false;

        AttachmentFileJournal.BeforePathMutationForTesting = path =>
        {
            if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(destinationPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Move(destinationDirectory, movedDirectory);
                CreateDirectoryJunction(destinationDirectory, outsideScope.Path);
                swapCompleted = true;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                swapBlocked = true;
                if (Directory.Exists(movedDirectory) &&
                    !Directory.Exists(destinationDirectory))
                {
                    Directory.Move(movedDirectory, destinationDirectory);
                }
            }
        };

        try
        {
            journal.Complete();
        }
        finally
        {
            AttachmentFileJournal.BeforePathMutationForTesting = null;
            if (swapCompleted)
                DeleteDirectoryLink(destinationDirectory);
        }

        Assert.True(swapBlocked || swapCompleted);
        if (swapBlocked)
        {
            Assert.False(File.Exists(destinationPath));
        }
        else
        {
            Assert.Equal(
                "%PDF-delete-target",
                await File.ReadAllTextAsync(
                    Path.Combine(movedDirectory, "receipt.pdf")));
            Assert.Single(Directory.EnumerateDirectories(
                Path.Combine(attachmentRoot, ".file-journals"),
                "attachment-files-*",
                SearchOption.TopDirectoryOnly));
        }
        Assert.Equal(
            "%PDF-outside-sentinel",
            await File.ReadAllTextAsync(outsidePath));
    }

    [Fact]
    public async Task StagedDelete_BeforeDatabaseCommit_DoesNotMoveOrDeleteOriginal()
    {
        using var scope = new TemporaryDirectory();
        var destinationPath = Path.Combine(scope.Path, "attachments", "receipt.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(destinationPath, "preserve-until-commit");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        journal.StageDelete(destinationPath);
        journal.Promote();

        Assert.Equal(
            "preserve-until-commit",
            await File.ReadAllTextAsync(destinationPath));

        journal.Rollback();
        Assert.True(File.Exists(destinationPath));
    }

    [Fact]
    public async Task StagedDelete_CompleteAfterDatabaseCommit_RemovesOriginal()
    {
        using var scope = new TemporaryDirectory();
        var destinationPath = Path.Combine(scope.Path, "attachments", "receipt.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllTextAsync(destinationPath, "delete-after-commit");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        journal.StageDelete(destinationPath);
        journal.Promote();
        Assert.True(File.Exists(destinationPath));

        journal.Complete();
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task ExplicitDatabaseCommit_CompleteAfterCommit_RemovesUniqueEvidenceAndJournal()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(
            attachmentRoot,
            ".file-journals");
        var evidencePath = Path.Combine(
            attachmentRoot,
            "inventory",
            "unique-proof.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        await File.WriteAllTextAsync(
            evidencePath,
            "%PDF-explicit-commit");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        var transfer = new LocalInventoryTransfer
        {
            Id = Guid.NewGuid(),
            TransferNumber = "EXPLICIT-COMMIT-001",
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            ReceiveEvidencePath = evidencePath,
            IsDirty = false
        };
        db.InventoryTransfers.Add(transfer);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        await using (var transaction =
                     await db.Database.BeginTransactionAsync())
        {
            journal.StageDelete(evidencePath);
            db.InventoryTransfers.Remove(
                (await db.InventoryTransfers
                    .IgnoreQueryFilters()
                    .SingleAsync(current => current.Id == transfer.Id)));
            await db.SaveChangesAsync();
            journal.Promote();
            await transaction.CommitAsync();
        }

        Assert.Null(db.Database.CurrentTransaction);
        await journal.CompleteAfterDatabaseCommitAsync(db);

        Assert.False(File.Exists(evidencePath));
        Assert.Empty(Directory.EnumerateDirectories(
            journalRoot,
            "attachment-files-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CompleteAfterDatabaseCommit_RejectsActiveDatabaseTransaction()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(
            attachmentRoot,
            ".file-journals");
        var evidencePath = Path.Combine(
            attachmentRoot,
            "inventory",
            "active-transaction-proof.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        await File.WriteAllTextAsync(
            evidencePath,
            "%PDF-active-transaction");

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        journal.StageDelete(evidencePath);
        journal.Promote();

        await using var transaction =
            await db.Database.BeginTransactionAsync();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => journal.CompleteAfterDatabaseCommitAsync(db));

        Assert.Contains("DB 트랜잭션", exception.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(evidencePath));
        journal.Rollback();
    }

    [Fact]
    public async Task CrossJournal_DeleteSnapshotCannotRemoveConcurrentIdenticalCommittedWrite()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(
            attachmentRoot,
            ".file-journals");
        var destinationPath = Path.Combine(
            attachmentRoot,
            "transactions",
            "shared-proof.pdf");
        var content = "%PDF-cross-journal-identical"u8.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, content);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        using var deleteJournal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        using var writeJournal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        deleteJournal.StageDelete(destinationPath);
        deleteJournal.Promote();

        var writeAttempted = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stageWriteTask = Task.Run(async () =>
        {
            writeAttempted.TrySetResult(true);
            await writeJournal.StageWriteAsync(
                destinationPath,
                content);
        });
        await writeAttempted.Task;
        var writeWaitedForDeleteSnapshot =
            await Task.WhenAny(
                stageWriteTask,
                Task.Delay(TimeSpan.FromMilliseconds(150))) != stageWriteTask;

        await deleteJournal.CompleteAfterDatabaseCommitAsync(db);
        await stageWriteTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(writeWaitedForDeleteSnapshot);
        Assert.False(File.Exists(destinationPath));

        writeJournal.Promote();
        var transactionId = Guid.NewGuid();
        db.Transactions.Add(new LocalTransaction
        {
            Id = transactionId,
            CustomerId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            IsDirty = false
        });
        db.TransactionAttachments.Add(new LocalTransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "shared-proof.pdf",
            StoredFileName = "shared-proof.pdf",
            StoredPath = destinationPath,
            MimeType = "application/pdf",
            FileSize = content.Length,
            IsDirty = false
        });
        await db.SaveChangesAsync();
        await writeJournal.CompleteAfterDatabaseCommitAsync(db);

        Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
        Assert.True(await db.TransactionAttachments
            .IgnoreQueryFilters()
            .AnyAsync(current => current.StoredPath == destinationPath));
        Assert.Empty(Directory.EnumerateDirectories(
            journalRoot,
            "attachment-files-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task CrossJournal_IdenticalWriteRejectsDeleteUntilCommittedReferenceSnapshot()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(
            attachmentRoot,
            ".file-journals");
        var destinationPath = Path.Combine(
            attachmentRoot,
            "transactions",
            "existing-shared-proof.pdf");
        var content = "%PDF-cross-journal-existing"u8.ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(destinationPath, content);

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        using var writeJournal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        using var deleteJournal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        await writeJournal.StageWriteAsync(destinationPath, content);
        writeJournal.Promote();

        var contention = Assert.Throws<AttachmentFileJournalContentionException>(() =>
            deleteJournal.StageDelete(destinationPath));
        Assert.Contains(
            "Retry the operation",
            contention.Message,
            StringComparison.Ordinal);

        var transactionId = Guid.NewGuid();
        db.Transactions.Add(new LocalTransaction
        {
            Id = transactionId,
            CustomerId = Guid.NewGuid(),
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            IsDirty = false
        });
        db.TransactionAttachments.Add(new LocalTransactionAttachment
        {
            Id = Guid.NewGuid(),
            TransactionId = transactionId,
            FileName = "existing-shared-proof.pdf",
            StoredFileName = "existing-shared-proof.pdf",
            StoredPath = destinationPath,
            MimeType = "application/pdf",
            FileSize = content.Length,
            IsDirty = false
        });
        await db.SaveChangesAsync();
        await writeJournal.CompleteAfterDatabaseCommitAsync(db);

        deleteJournal.StageDelete(destinationPath);
        deleteJournal.Promote();
        await deleteJournal.CompleteAfterDatabaseCommitAsync(db);

        Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
        Assert.Empty(Directory.EnumerateDirectories(
            journalRoot,
            "attachment-files-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Recovery_UnreferencedPromotedFileWithMatchingHash_IsRemoved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "orphan.pdf");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, "%PDF-orphan"u8.ToArray());
        journal.Promote();
        Assert.True(File.Exists(destinationPath));
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);

        Assert.False(File.Exists(destinationPath));
        Assert.Empty(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Recovery_DatabaseReferencedPromotedFile_IsNeverRemoved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "committed.pdf");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, "%PDF-committed"u8.ToArray());
        journal.Promote();
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            [destinationPath]);

        Assert.Equal(
            "%PDF-committed",
            await File.ReadAllTextAsync(destinationPath));
        Assert.Empty(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Recovery_ContentChangedAfterPromotion_IsPreservedForManualReview()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "changed.pdf");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, "%PDF-original"u8.ToArray());
        journal.Promote();
        await File.WriteAllTextAsync(destinationPath, "%PDF-changed");
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);

        Assert.Equal("%PDF-changed", await File.ReadAllTextAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Recovery_SameContentReplacementAfterPromotion_IsNeverRemoved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "same-bytes-recovery.pdf");
        var displacedPath = Path.Combine(attachmentRoot, "displaced-original.pdf");
        var content = "%PDF-identical-recovery"u8.ToArray();
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, content);
        journal.Promote();
        File.Move(destinationPath, displacedPath);
        await File.WriteAllBytesAsync(destinationPath, content);
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);

        Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Rollback_ContentChangedAfterPromotion_PreservesReplacementForManualReview()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "changed-before-rollback.pdf");
        using var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, "%PDF-promoted"u8.ToArray());
        journal.Promote();
        await File.WriteAllTextAsync(destinationPath, "%PDF-replacement");

        journal.Rollback();

        Assert.Equal(
            "%PDF-replacement",
            await File.ReadAllTextAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Rollback_SameContentReplacementAfterPromotion_IsNeverRemoved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "same-bytes-rollback.pdf");
        var displacedPath = Path.Combine(attachmentRoot, "displaced-original.pdf");
        var content = "%PDF-identical-rollback"u8.ToArray();
        using var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, content);
        journal.Promote();
        File.Move(destinationPath, displacedPath);
        await File.WriteAllBytesAsync(destinationPath, content);

        journal.Rollback();

        Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Complete_DeleteTargetChangedAfterPromotion_PreservesReplacementForManualReview()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "changed-before-complete.pdf");
        Directory.CreateDirectory(attachmentRoot);
        await File.WriteAllTextAsync(destinationPath, "%PDF-original");
        using var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        journal.StageDelete(destinationPath);
        journal.Promote();
        await File.WriteAllTextAsync(destinationPath, "%PDF-replacement");

        journal.Complete();

        Assert.Equal(
            "%PDF-replacement",
            await File.ReadAllTextAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Complete_SameContentReplacementOfDeleteTarget_IsNeverRemoved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "same-bytes-complete.pdf");
        var displacedPath = Path.Combine(attachmentRoot, "displaced-original.pdf");
        var content = "%PDF-identical-complete"u8.ToArray();
        Directory.CreateDirectory(attachmentRoot);
        await File.WriteAllBytesAsync(destinationPath, content);
        using var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        journal.StageDelete(destinationPath);
        journal.Promote();
        File.Move(destinationPath, displacedPath);
        await File.WriteAllBytesAsync(destinationPath, content);

        journal.Complete();

        Assert.Equal(content, await File.ReadAllBytesAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Recovery_Version1ManifestWithoutFileId_IsPreserved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "legacy-manifest.pdf");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-legacy-manifest"u8.ToArray());
        journal.Promote();
        var journalDirectory = Assert.Single(
            Directory.EnumerateDirectories(journalRoot));
        var manifestPath = Path.Combine(journalDirectory, "recovery.json");
        var manifest = await File.ReadAllTextAsync(manifestPath);
        await File.WriteAllTextAsync(
            manifestPath,
            manifest.Replace("\"Version\":2", "\"Version\":1"));
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);

        Assert.True(File.Exists(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task Recovery_ActiveJournal_IsNotSwept()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "active.pdf");
        using var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        await journal.StageWriteAsync(destinationPath, "%PDF-active"u8.ToArray());
        journal.Promote();

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);

        Assert.Equal("%PDF-active", await File.ReadAllTextAsync(destinationPath));
        journal.Rollback();
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task Recovery_ConcurrentJournalJunctionSwap_IsBlockedByMutationLease()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "orphan.pdf");
        var outsideSentinel = Path.Combine(outsideScope.Path, "sentinel.txt");
        await File.WriteAllTextAsync(outsideSentinel, "preserve");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);
        await journal.StageWriteAsync(
            destinationPath,
            "%PDF-recovery-parent-lease"u8.ToArray());
        journal.Promote();
        var journalDirectory = Assert.Single(
            Directory.EnumerateDirectories(
                journalRoot,
                "attachment-files-*",
                SearchOption.TopDirectoryOnly));
        var movedJournalDirectory = Path.Combine(
            scope.Path,
            "moved-journal");
        journal.PreserveForRecovery();
        var swapBlocked = false;

        AttachmentFileJournal.BeforePathMutationForTesting = path =>
        {
            if (!string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(journalDirectory),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                Directory.Move(journalDirectory, movedJournalDirectory);
                CreateDirectoryJunction(journalDirectory, outsideScope.Path);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException)
            {
                swapBlocked = true;
            }
        };

        try
        {
            AttachmentFileJournal.RecoverIncompleteJournals(
                journalRoot,
                attachmentRoot,
                []);
        }
        finally
        {
            AttachmentFileJournal.BeforePathMutationForTesting = null;
            journal.Dispose();
        }

        Assert.True(swapBlocked);
        Assert.False(File.Exists(destinationPath));
        Assert.Equal("preserve", await File.ReadAllTextAsync(outsideSentinel));
        Assert.Empty(Directory.EnumerateDirectories(
            journalRoot,
            "attachment-files-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task Recovery_AfterChildProcessHardKill_ReacquiresRootLeaseAndRecoversManifest()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var journalDirectory = Path.Combine(
            journalRoot,
            "attachment-files-child-crash");
        var destinationPath = Path.Combine(
            attachmentRoot,
            "hard-kill-orphan.pdf");
        var stagedPath = Path.Combine(
            journalDirectory,
            "child-crash.stage");
        var manifestPath = Path.Combine(
            journalDirectory,
            "recovery.json");
        var activeLeasePath = Path.Combine(
            journalDirectory,
            "active.lock");
        var readyPath = Path.Combine(scope.Path, "child-ready.txt");
        var stagedReadyPath = Path.Combine(
            scope.Path,
            "child-staged-ready.txt");
        var promoteSignalPath = Path.Combine(
            scope.Path,
            "child-promote.txt");
        var rootLeasePath = Path.Combine(journalRoot, "mutation-root.lock");
        var content = "%PDF-hard-kill-recovery"u8.ToArray();
        var databasePath = Path.Combine(
            scope.Path,
            "hard-kill-marker.db");
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite($"Data Source={databasePath}")
            .Options;
        await using (var setupDb = new LocalDbContext(options))
            await setupDb.Database.EnsureCreatedAsync();
        await using var markerDb = new LocalDbContext(options);
        await using var markerTransaction =
            await markerDb.Database.BeginTransactionAsync();
        markerDb.Settings.Add(new LocalSetting
        {
            Key =
                $"__internal.attachment-file-commit.{Guid.NewGuid():N}",
            Value = DateTime.UtcNow.ToString("O")
        });
        await markerDb.SaveChangesAsync();

        var powershellPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        var startInfo = new ProcessStartInfo(powershellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(
            "[IO.Directory]::CreateDirectory(" +
            "$env:GEORAEPLAN_TEST_JOURNAL_ROOT) | Out-Null; " +
            "[IO.Directory]::CreateDirectory(" +
            "$env:GEORAEPLAN_TEST_JOURNAL_DIR) | Out-Null; " +
            "[IO.Directory]::CreateDirectory(" +
            "[IO.Path]::GetDirectoryName(" +
            "$env:GEORAEPLAN_TEST_DESTINATION)) | Out-Null; " +
            "$rootLease = [IO.File]::Open(" +
            "$env:GEORAEPLAN_TEST_ROOT_LEASE," +
            "[IO.FileMode]::OpenOrCreate," +
            "[IO.FileAccess]::ReadWrite," +
            "[IO.FileShare]::None); " +
            "$activeLease = [IO.File]::Open(" +
            "$env:GEORAEPLAN_TEST_ACTIVE_LEASE," +
            "[IO.FileMode]::CreateNew," +
            "[IO.FileAccess]::ReadWrite," +
            "[IO.FileShare]::None); " +
            "[IO.File]::WriteAllBytes(" +
            "$env:GEORAEPLAN_TEST_STAGED_PATH," +
            "[Convert]::FromBase64String(" +
            "$env:GEORAEPLAN_TEST_CONTENT)); " +
            "[IO.File]::WriteAllText(" +
            "$env:GEORAEPLAN_TEST_STAGED_READY,'ready'); " +
            "while (!(Test-Path -LiteralPath " +
            "$env:GEORAEPLAN_TEST_PROMOTE_SIGNAL)) { " +
            "Start-Sleep -Milliseconds 25 }; " +
            "[IO.File]::Move(" +
            "$env:GEORAEPLAN_TEST_STAGED_PATH," +
            "$env:GEORAEPLAN_TEST_DESTINATION); " +
            "[IO.File]::WriteAllText(" +
            "$env:GEORAEPLAN_TEST_READY,'ready'); " +
            "while ($true) { Start-Sleep -Seconds 1 }");
        startInfo.Environment["GEORAEPLAN_TEST_JOURNAL_ROOT"] =
            journalRoot;
        startInfo.Environment["GEORAEPLAN_TEST_JOURNAL_DIR"] =
            journalDirectory;
        startInfo.Environment["GEORAEPLAN_TEST_ROOT_LEASE"] = rootLeasePath;
        startInfo.Environment["GEORAEPLAN_TEST_ACTIVE_LEASE"] =
            activeLeasePath;
        startInfo.Environment["GEORAEPLAN_TEST_STAGED_PATH"] = stagedPath;
        startInfo.Environment["GEORAEPLAN_TEST_DESTINATION"] =
            destinationPath;
        startInfo.Environment["GEORAEPLAN_TEST_CONTENT"] =
            Convert.ToBase64String(content);
        startInfo.Environment["GEORAEPLAN_TEST_STAGED_READY"] =
            stagedReadyPath;
        startInfo.Environment["GEORAEPLAN_TEST_PROMOTE_SIGNAL"] =
            promoteSignalPath;
        startInfo.Environment["GEORAEPLAN_TEST_READY"] = readyPath;

        using var child = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start the attachment root-lease child process.");
        try
        {
            var stagedDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(stagedReadyPath) &&
                   DateTime.UtcNow < stagedDeadline)
            {
                await Task.Delay(25);
            }

            Assert.True(
                File.Exists(stagedReadyPath),
                "The child process did not create its staged journal file.");
            var stagedIdentity =
                ReadAttachmentFileIdentityForTesting(stagedPath);
            await File.WriteAllTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    Version = 2,
                    Writes = new[]
                    {
                        new
                        {
                            DestinationPath = destinationPath,
                            stagedIdentity.Length,
                            stagedIdentity.Sha256,
                            stagedIdentity.VolumeSerialNumber,
                            stagedIdentity.FileId
                        }
                    },
                    Deletes = Array.Empty<object>()
                }));
            await File.WriteAllTextAsync(
                promoteSignalPath,
                "promote");

            var readyDeadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(readyPath) &&
                   DateTime.UtcNow < readyDeadline)
            {
                await Task.Delay(25);
            }

            Assert.True(
                File.Exists(readyPath),
                "The child process did not promote its staged journal file.");

            await using (var blockedRecoveryDb =
                         new LocalDbContext(options))
            {
                await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
                    blockedRecoveryDb,
                    journalRoot,
                    attachmentRoot);
            }
            Assert.Equal(content, await File.ReadAllBytesAsync(
                destinationPath));
            Assert.Single(Directory.EnumerateDirectories(
                journalRoot,
                "attachment-files-*",
                SearchOption.TopDirectoryOnly));

            child.Kill(entireProcessTree: true);
            Assert.True(child.WaitForExit(milliseconds: 5000));
            await markerTransaction.RollbackAsync();

            await using var recoveryDb = new LocalDbContext(options);
            await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
                recoveryDb,
                journalRoot,
                attachmentRoot);

            Assert.False(File.Exists(destinationPath));
            Assert.Empty(Directory.EnumerateDirectories(
                journalRoot,
                "attachment-files-*",
                SearchOption.TopDirectoryOnly));
            Assert.False(await recoveryDb.Settings.AnyAsync(setting =>
                setting.Key.StartsWith(
                    "__internal.attachment-file-commit.")));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(milliseconds: 5000);
            }
        }
    }

    [Fact]
    public void Recovery_JournalRootJunction_IsRejectedBeforeTraversal()
    {
        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var actualJournalRoot = Path.Combine(
            outsideScope.Path,
            "actual-journals");
        var journalRootLink = Path.Combine(
            scope.Path,
            "journal-root-link");
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var sentinelPath = Path.Combine(
            actualJournalRoot,
            "must-not-be-traversed.txt");
        Directory.CreateDirectory(actualJournalRoot);
        Directory.CreateDirectory(attachmentRoot);
        File.WriteAllText(sentinelPath, "preserve");
        CreateDirectoryJunction(journalRootLink, actualJournalRoot);

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                AttachmentFileJournal.RecoverIncompleteJournals(
                    journalRootLink,
                    attachmentRoot,
                    []));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
        }
        finally
        {
            DeleteDirectoryLink(journalRootLink);
        }
    }

    [Fact]
    public void Recovery_JournalRootAncestorJunction_IsRejectedBeforeTraversal()
    {
        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var actualParent = Path.Combine(
            outsideScope.Path,
            "actual-parent");
        var actualJournalRoot = Path.Combine(
            actualParent,
            "nested-journals");
        var linkedParent = Path.Combine(scope.Path, "linked-parent");
        var journalRootThroughLink = Path.Combine(
            linkedParent,
            "nested-journals");
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var sentinelPath = Path.Combine(
            actualJournalRoot,
            "must-not-be-traversed.txt");
        Directory.CreateDirectory(actualJournalRoot);
        Directory.CreateDirectory(attachmentRoot);
        File.WriteAllText(sentinelPath, "preserve");
        CreateDirectoryJunction(linkedParent, actualParent);

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                AttachmentFileJournal.RecoverIncompleteJournals(
                    journalRootThroughLink,
                    attachmentRoot,
                    []));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
        }
        finally
        {
            DeleteDirectoryLink(linkedParent);
        }
    }

    [Fact]
    public void Recovery_AllowedRootAncestorJunction_IsRejectedBeforeTraversal()
    {
        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var journalRoot = Path.Combine(scope.Path, "journals");
        var actualParent = Path.Combine(
            outsideScope.Path,
            "actual-parent");
        var actualAttachmentRoot = Path.Combine(
            actualParent,
            "attachments");
        var linkedParent = Path.Combine(scope.Path, "linked-parent");
        var attachmentRootThroughLink = Path.Combine(
            linkedParent,
            "attachments");
        var sentinelPath = Path.Combine(
            actualAttachmentRoot,
            "must-not-be-mutated.txt");
        Directory.CreateDirectory(journalRoot);
        Directory.CreateDirectory(actualAttachmentRoot);
        File.WriteAllText(sentinelPath, "preserve");
        CreateDirectoryJunction(linkedParent, actualParent);

        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                AttachmentFileJournal.RecoverIncompleteJournals(
                    journalRoot,
                    attachmentRootThroughLink,
                    []));
            Assert.Equal("preserve", File.ReadAllText(sentinelPath));
        }
        finally
        {
            DeleteDirectoryLink(linkedParent);
        }
    }

    [Fact]
    public async Task Recovery_AmbiguousDeleteStillReferencedByDatabase_IsPreserved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var originalPath = Path.Combine(attachmentRoot, "still-referenced.pdf");
        Directory.CreateDirectory(attachmentRoot);
        await File.WriteAllTextAsync(originalPath, "%PDF-still-referenced");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        journal.StageDelete(originalPath);
        journal.Promote();
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            [originalPath]);

        Assert.Equal(
            "%PDF-still-referenced",
            await File.ReadAllTextAsync(originalPath));
    }

    [Fact]
    public async Task Recovery_AmbiguousDeleteNoLongerReferenced_IsCompleted()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var originalPath = Path.Combine(attachmentRoot, "committed-delete.pdf");
        Directory.CreateDirectory(attachmentRoot);
        await File.WriteAllTextAsync(originalPath, "%PDF-delete-committed");
        var journal = new AttachmentFileJournal(journalRoot, attachmentRoot);

        journal.StageDelete(originalPath);
        journal.Promote();
        ReleaseJournalLeaseToSimulateProcessExit(journal);

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);

        Assert.False(File.Exists(originalPath));
        Assert.Empty(Directory.EnumerateDirectories(journalRoot));
    }

    [Fact]
    public async Task CommitAmbiguity_WhenIndependentReadCannotConfirm_PreservesForDurableRecovery()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(scope.Path, "journals");
        var destinationPath = Path.Combine(attachmentRoot, "unknown-commit.pdf");
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        using (var journal = new AttachmentFileJournal(journalRoot, attachmentRoot))
        {
            await journal.StageWriteAsync(
                destinationPath,
                "%PDF-unknown-commit"u8.ToArray());
            journal.Promote();

            await journal.ResolveCommitAmbiguityAsync(
                db,
                CancellationToken.None);
        }

        Assert.Equal(
            "%PDF-unknown-commit",
            await File.ReadAllTextAsync(destinationPath));
        Assert.Single(Directory.EnumerateDirectories(journalRoot));

        AttachmentFileJournal.RecoverIncompleteJournals(
            journalRoot,
            attachmentRoot,
            []);
        Assert.False(File.Exists(destinationPath));
    }

    [Fact]
    public async Task CommitEvidence_ZeroFileOperationCommitThenThrow_ResolvesCommitted()
    {
        using var scope = new TemporaryDirectory();
        var databasePath = Path.Combine(scope.Path, "zero-op.db");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        await using (var setupDb = new LocalDbContext(baseOptions))
            await setupDb.Database.EnsureCreatedAsync();

        var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ThrowAfterCommitInterceptor())
            .Options;
        await using var db = new LocalDbContext(ambiguousOptions);
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journals"),
            Path.Combine(scope.Path, "attachments"));

        await journal.StageCommitEvidenceAsync(db);
        journal.Promote();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.CommitAsync());

        var resolution = await journal.ResolveCommitAmbiguityAsync(db);

        Assert.Equal(AttachmentCommitResolution.Committed, resolution);
        await using var verificationDb = new LocalDbContext(baseOptions);
        Assert.False(await verificationDb.Settings
            .AnyAsync(setting =>
                setting.Key.StartsWith("__internal.attachment-file-commit.")));
    }

    [Fact]
    public async Task Recovery_AbsentRootConcurrentCommittedMarker_IsNotDeletedWithoutRootLease()
    {
        using var scope = new TemporaryDirectory();
        var databasePath = Path.Combine(scope.Path, "recovery-root-race.db");
        var connectionString = $"Data Source={databasePath}";
        var options = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        Directory.CreateDirectory(attachmentRoot);
        await using (var setupDb = new LocalDbContext(options))
            await setupDb.Database.EnsureCreatedAsync();

        AttachmentFileJournal? writer = null;
        AttachmentFileJournal.AfterRecoveryRootEnsuredBeforeLeaseAsyncForTesting =
            async (observedJournalRoot, ct) =>
            {
                if (!string.Equals(
                        Path.GetFullPath(observedJournalRoot),
                        Path.GetFullPath(journalRoot),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                await using var writerDb = new LocalDbContext(options);
                await using var transaction =
                    await writerDb.Database.BeginTransactionAsync(ct);
                writer = new AttachmentFileJournal(
                    journalRoot,
                    attachmentRoot);
                await writer.StageCommitEvidenceAsync(writerDb, ct);
                writer.Promote();
                await transaction.CommitAsync(ct);
            };

        try
        {
            await using var recoveryDb = new LocalDbContext(options);
            await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
                recoveryDb,
                journalRoot,
                attachmentRoot);

            await using var verificationDb = new LocalDbContext(options);
            Assert.Equal(
                1,
                await verificationDb.Settings.CountAsync(setting =>
                    setting.Key.StartsWith(
                        "__internal.attachment-file-commit.")));

            Assert.NotNull(writer);
            await writer!.CompleteAfterDatabaseCommitAsync(verificationDb);
            Assert.False(await verificationDb.Settings.AnyAsync(setting =>
                setting.Key.StartsWith(
                    "__internal.attachment-file-commit.")));
        }
        finally
        {
            AttachmentFileJournal
                .AfterRecoveryRootEnsuredBeforeLeaseAsyncForTesting = null;
            writer?.Dispose();
        }
    }

    [Fact]
    public async Task CommitEvidence_SharedReferenceCommitThenThrow_PreservesSharedFile()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        var sharedPath = Path.Combine(attachmentRoot, "shared.pdf");
        var databasePath = Path.Combine(scope.Path, "shared-reference.db");
        Directory.CreateDirectory(attachmentRoot);
        await File.WriteAllTextAsync(sharedPath, "%PDF-shared-reference");
        var connectionString = $"Data Source={databasePath}";
        var baseOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .Options;
        var removedTransactionId = Guid.NewGuid();
        var survivingTransactionId = Guid.NewGuid();
        var removedAttachmentId = Guid.NewGuid();
        await using (var setupDb = new LocalDbContext(baseOptions))
        {
            await setupDb.Database.EnsureCreatedAsync();
            setupDb.Transactions.AddRange(
                new LocalTransaction
                {
                    Id = removedTransactionId,
                    CustomerId = Guid.NewGuid(),
                    TransactionKind = PaymentFlowConstants.TransactionKindReceipt
                },
                new LocalTransaction
                {
                    Id = survivingTransactionId,
                    CustomerId = Guid.NewGuid(),
                    TransactionKind = PaymentFlowConstants.TransactionKindReceipt
                });
            setupDb.TransactionAttachments.AddRange(
                new LocalTransactionAttachment
                {
                    Id = removedAttachmentId,
                    TransactionId = removedTransactionId,
                    StoredPath = sharedPath,
                    FileName = "shared.pdf",
                    StoredFileName = "shared.pdf",
                    FileSize = new FileInfo(sharedPath).Length
                },
                new LocalTransactionAttachment
                {
                    Id = Guid.NewGuid(),
                    TransactionId = survivingTransactionId,
                    StoredPath = sharedPath,
                    FileName = "shared.pdf",
                    StoredFileName = "shared.pdf",
                    FileSize = new FileInfo(sharedPath).Length
                });
            await setupDb.SaveChangesAsync();
        }

        var ambiguousOptions = new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new ThrowAfterCommitInterceptor())
            .Options;
        await using var db = new LocalDbContext(ambiguousOptions);
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);
        var removed = await db.TransactionAttachments
            .IgnoreQueryFilters()
            .SingleAsync(current => current.Id == removedAttachmentId);
        db.TransactionAttachments.Remove(removed);
        journal.StageDelete(sharedPath);
        await db.SaveChangesAsync();
        await journal.StageCommitEvidenceAsync(db);
        journal.Promote();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            transaction.CommitAsync());

        var resolution = await journal.ResolveCommitAmbiguityAsync(db);

        Assert.Equal(AttachmentCommitResolution.Committed, resolution);
        Assert.Equal(
            "%PDF-shared-reference",
            await File.ReadAllTextAsync(sharedPath));
        await using var verificationDb = new LocalDbContext(baseOptions);
        Assert.Single(await verificationDb.TransactionAttachments
            .IgnoreQueryFilters()
            .Where(current => current.StoredPath == sharedPath)
            .ToListAsync());
    }

    [Fact]
    public async Task MutationOutsideAllowedAttachmentRoot_IsRejectedAndPreserved()
    {
        using var scope = new TemporaryDirectory();
        using var outsideScope = new TemporaryDirectory();
        var outsidePath = Path.Combine(outsideScope.Path, "must-stay.txt");
        await File.WriteAllTextAsync(outsidePath, "preserve");
        using var journal = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journal"),
            scope.Path);

        Assert.Throws<InvalidOperationException>(() => journal.StageDelete(outsidePath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.StageWriteAsync(outsidePath, "replacement"u8.ToArray()));

        Assert.Equal("preserve", await File.ReadAllTextAsync(outsidePath));
    }

    [Fact]
    public async Task MutationInsidePrivateJournalRoot_IsRejectedAndPreserved()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        Directory.CreateDirectory(journalRoot);
        var sentinelPath = Path.Combine(journalRoot, "other-journal-sentinel.json");
        await File.WriteAllTextAsync(sentinelPath, "preserve");

        using var journal = new AttachmentFileJournal(
            journalRoot,
            attachmentRoot);

        Assert.Throws<InvalidOperationException>(() =>
            journal.StageDelete(sentinelPath));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            journal.StageWriteAsync(
                sentinelPath,
                "replacement"u8.ToArray()));
        Assert.Equal("preserve", await File.ReadAllTextAsync(sentinelPath));
    }

    [Fact]
    public async Task Recovery_InventoryTransferStillReferencesEvidence_PreservesFile()
    {
        using var scope = new TemporaryDirectory();
        var attachmentRoot = Path.Combine(scope.Path, "attachments");
        var journalRoot = Path.Combine(attachmentRoot, ".file-journals");
        var evidenceDirectory = Path.Combine(attachmentRoot, "inventory");
        var evidencePath = Path.Combine(evidenceDirectory, "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\ninventory evidence"u8.ToArray());

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = Guid.NewGuid(),
            TransferNumber = "RECOVERY-EVIDENCE-001",
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            ReceiveEvidencePath = evidencePath,
            IsDirty = false
        });
        await db.SaveChangesAsync();

        using (var journal = new AttachmentFileJournal(
                   journalRoot,
                   attachmentRoot))
        {
            journal.StageDelete(evidencePath);
            journal.Promote();
            journal.PreserveForRecovery();
        }

        await AttachmentFileJournal.RecoverIncompleteJournalsAsync(
            db,
            journalRoot,
            attachmentRoot);

        Assert.True(File.Exists(evidencePath));
        Assert.Empty(Directory.EnumerateDirectories(
            journalRoot,
            "attachment-files-*",
            SearchOption.TopDirectoryOnly));
    }

    [Fact]
    public async Task PublicServerPurgeInventoryTransfer_DatabaseDeleteFailure_PreservesEvidenceFileAndRow()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"purge-failure-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\ninventory purge rollback"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "PURGE-FAILURE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_inventory_transfer_delete
                BEFORE DELETE ON InventoryTransfers
                BEGIN
                    SELECT RAISE(ABORT, 'blocked inventory transfer delete');
                END;
                """);

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.ApplyServerPurgeRecycleBinEntryAsync(
                    RecycleBinEntityKind.InventoryTransfer,
                    transferId));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(evidencePath));
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task PublicServerPurgeInventoryTransfer_UniqueEvidence_DeletesAfterCommitAndCleansJournal()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"purge-success-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            evidenceDirectory,
            "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nunique server purge evidence"u8.ToArray());
        var journalsBefore = CaptureAttachmentJournalDirectories();

        try
        {
            await using var connection = new SqliteConnection(
                "Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "PURGE-SUCCESS-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                transferId);

            Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(evidencePath));
            db.ChangeTracker.Clear();
            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            Assert.Empty(
                CaptureAttachmentJournalDirectories()
                    .Except(journalsBefore, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task PublicServerPurgeInventoryTransfer_OutsideAttachmentRoot_PreservesExternalFile()
    {
        using var scope = new TemporaryDirectory();
        var externalEvidencePath = Path.Combine(scope.Path, "external-proof.pdf");
        await File.WriteAllBytesAsync(
            externalEvidencePath,
            "%PDF-1.4\nexternal evidence must survive"u8.ToArray());

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        var transferId = Guid.NewGuid();
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferNumber = "PURGE-OUTSIDE-ROOT-001",
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            ReceiveEvidencePath = externalEvidencePath,
            IsDirty = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());
        var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
            RecycleBinEntityKind.InventoryTransfer,
            transferId);

        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        Assert.True(File.Exists(externalEvidencePath));
        Assert.False(await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == transferId));
    }

    [Fact]
    public async Task PublicPermanentDeleteInventoryTransfer_DatabaseDeleteFailure_PreservesEvidenceFileAndRow()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"local-purge-failure-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nlocal inventory purge rollback"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "LOCAL-PURGE-FAILURE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_local_inventory_transfer_delete
                BEFORE DELETE ON InventoryTransfers
                BEGIN
                    SELECT RAISE(ABORT, 'blocked local inventory transfer delete');
                END;
                """);

            var session = CreateAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.PermanentlyDeleteRecycleBinEntryAsync(
                    RecycleBinEntityKind.InventoryTransfer,
                    transferId,
                    session));

            db.ChangeTracker.Clear();
            Assert.True(File.Exists(evidencePath));
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task PublicPermanentDeleteInventoryTransfer_UniqueEvidence_DeletesAfterCommitAndCleansJournal()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"local-purge-success-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            evidenceDirectory,
            "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nunique local purge evidence"u8.ToArray());
        var journalsBefore = CaptureAttachmentJournalDirectories();

        try
        {
            await using var connection = new SqliteConnection(
                "Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "LOCAL-PURGE-SUCCESS-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                transferId,
                session);

            Assert.True(result.Success, result.Message);
            Assert.False(File.Exists(evidencePath));
            db.ChangeTracker.Clear();
            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            Assert.Empty(
                CaptureAttachmentJournalDirectories()
                    .Except(journalsBefore, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task PublicPermanentDeleteInventoryTransfer_OutsideAttachmentRoot_PreservesExternalFile()
    {
        using var scope = new TemporaryDirectory();
        var externalEvidencePath = Path.Combine(scope.Path, "external-proof.pdf");
        await File.WriteAllBytesAsync(
            externalEvidencePath,
            "%PDF-1.4\nexternal local evidence must survive"u8.ToArray());

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        var transferId = Guid.NewGuid();
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferNumber = "LOCAL-PURGE-OUTSIDE-ROOT-001",
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            ReceiveEvidencePath = externalEvidencePath,
            IsDeleted = true,
            IsDirty = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var session = CreateAdminSession();
        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            session);
        var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
            RecycleBinEntityKind.InventoryTransfer,
            transferId,
            session);

        Assert.True(result.Success, result.Message);
        db.ChangeTracker.Clear();
        Assert.True(File.Exists(externalEvidencePath));
        Assert.False(await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == transferId));
    }

    [Fact]
    public async Task PublicPermanentDeleteTransaction_SharedAttachmentPath_PreservesSurvivingReference()
    {
        var attachmentDirectory = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            $"local-shared-reference-{Guid.NewGuid():N}");
        var attachmentPath = Path.Combine(attachmentDirectory, "shared-proof.pdf");
        Directory.CreateDirectory(attachmentDirectory);
        await File.WriteAllBytesAsync(
            attachmentPath,
            "%PDF-1.4\nshared local transaction evidence"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var purgedTransactionId = Guid.NewGuid();
            var survivingTransactionId = Guid.NewGuid();
            db.Transactions.AddRange(
                new LocalTransaction
                {
                    Id = purgedTransactionId,
                    CustomerId = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                    IsDeleted = true,
                    IsDirty = false
                },
                new LocalTransaction
                {
                    Id = survivingTransactionId,
                    CustomerId = Guid.NewGuid(),
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    OfficeCode = OfficeCodeCatalog.Usenet,
                    ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                    TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
                    IsDeleted = false,
                    IsDirty = false
                });
            db.TransactionAttachments.AddRange(
                new LocalTransactionAttachment
                {
                    Id = Guid.NewGuid(),
                    TransactionId = purgedTransactionId,
                    FileName = "shared-proof.pdf",
                    StoredFileName = "shared-proof.pdf",
                    StoredPath = attachmentPath,
                    MimeType = "application/pdf",
                    FileSize = new FileInfo(attachmentPath).Length,
                    IsDeleted = true,
                    IsDirty = false
                },
                new LocalTransactionAttachment
                {
                    Id = Guid.NewGuid(),
                    TransactionId = survivingTransactionId,
                    FileName = "shared-proof.pdf",
                    StoredFileName = "shared-proof.pdf",
                    StoredPath = attachmentPath,
                    MimeType = "application/pdf",
                    FileSize = new FileInfo(attachmentPath).Length,
                    IsDeleted = false,
                    IsDirty = false
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var session = CreateAdminSession();
            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                session);
            var result = await service.PermanentlyDeleteRecycleBinEntryAsync(
                RecycleBinEntityKind.Transaction,
                purgedTransactionId,
                session);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == purgedTransactionId));
            Assert.True(await db.Transactions
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == survivingTransactionId));
            Assert.True(await db.TransactionAttachments
                .IgnoreQueryFilters()
                .AnyAsync(current => current.TransactionId == survivingTransactionId));
            Assert.True(File.Exists(attachmentPath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(attachmentDirectory))
                    Directory.Delete(attachmentDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task PublicServerPurgeInventoryTransfer_SharedEvidencePath_PreservesSurvivingReference()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"shared-reference-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "shared-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nshared inventory evidence"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var purgedTransferId = Guid.NewGuid();
            var survivingTransferId = Guid.NewGuid();
            db.InventoryTransfers.AddRange(
                new LocalInventoryTransfer
                {
                    Id = purgedTransferId,
                    TransferNumber = "PURGE-SHARED-001",
                    TransferDate = DateOnly.FromDateTime(DateTime.Today),
                    ReceiveEvidencePath = evidencePath,
                    IsDirty = false
                },
                new LocalInventoryTransfer
                {
                    Id = survivingTransferId,
                    TransferNumber = "PURGE-SHARED-002",
                    TransferDate = DateOnly.FromDateTime(DateTime.Today),
                    ReceiveEvidencePath = evidencePath,
                    IsDirty = false
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            var result = await service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.InventoryTransfer,
                purgedTransferId);

            Assert.True(result.Success, result.Message);
            db.ChangeTracker.Clear();
            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == purgedTransferId));
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == survivingTransferId));
            Assert.True(File.Exists(evidencePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task SharedMirrorReset_Committed_RemovesInventoryEvidenceFileAndRow()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"shared-reset-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "obsolete-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nobsolete inventory evidence"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "RESET-EVIDENCE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            await service.ResetSharedMirrorCacheAsync();

            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            Assert.False(File.Exists(evidencePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task SharedMirrorReset_ConflictOwnedInventoryEvidence_PreservesFileAndConflict()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            $"shared-reset-conflict-owned-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            evidenceDirectory,
            "conflict-owned-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        var expectedEvidence =
            "%PDF-1.4\nconflict-owned inventory evidence"u8.ToArray();
        await File.WriteAllBytesAsync(evidencePath, expectedEvidence);

        try
        {
            await using var connection = new SqliteConnection(
                "Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "RESET-CONFLICT-EVIDENCE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            db.InventoryTransferTombstoneConflicts.Add(
                new LocalInventoryTransferTombstoneConflict
                {
                    TransferId = transferId,
                    BusinessDatabaseName = "USENET",
                    TenantCode = TenantScopeCatalog.UsenetGroup,
                    SourceOfficeCode = OfficeCodeCatalog.Usenet,
                    TargetOfficeCode = OfficeCodeCatalog.Yeonsu,
                    ArchivedReceiveEvidencePath = evidencePath,
                    Status = InventoryTransferTombstoneConflictPolicy
                        .UnresolvedStatus,
                    LocalSnapshotJson = "{}",
                    ServerTombstoneJson = "{}"
                });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            await service.ResetSharedMirrorCacheAsync();

            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            var conflict = await db.InventoryTransferTombstoneConflicts
                .AsNoTracking()
                .SingleAsync(current =>
                    current.TransferId == transferId &&
                    current.BusinessDatabaseName == "USENET");
            Assert.Equal(evidencePath, conflict.ArchivedReceiveEvidencePath);
            Assert.True(File.Exists(evidencePath));
            Assert.Equal(
                expectedEvidence,
                await File.ReadAllBytesAsync(evidencePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task SharedMirrorReset_ReinsertedInventoryEvidence_PreservesCommittedFileAndCleansJournal()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"shared-reset-reinsert-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(
            evidenceDirectory,
            "reinserted-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nreinserted inventory evidence"u8.ToArray());
        var journalsBefore = CaptureAttachmentJournalDirectories();

        try
        {
            await using var connection = new SqliteConnection(
                "Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();
            var oldTransferId = Guid.NewGuid();
            var replacementTransferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = oldTransferId,
                TransferNumber = "RESET-REINSERT-OLD",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());
            await using var transaction =
                await db.Database.BeginTransactionAsync();
            using var attachmentFiles = new AttachmentFileJournal(
                AppPaths.AttachmentFileJournalDir,
                AppPaths.AttachmentsDir);
            await service.ResetSharedMirrorCacheWithAttachmentJournalAsync(
                attachmentFiles);
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = replacementTransferId,
                TransferNumber = "RESET-REINSERT-NEW",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            attachmentFiles.Promote();
            await transaction.CommitAsync();
            await transaction.DisposeAsync();
            await attachmentFiles.CompleteAfterDatabaseCommitAsync(db);

            db.ChangeTracker.Clear();
            Assert.False(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == oldTransferId));
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == replacementTransferId));
            Assert.True(File.Exists(evidencePath));
            Assert.Empty(
                CaptureAttachmentJournalDirectories()
                    .Except(journalsBefore, StringComparer.OrdinalIgnoreCase));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task SharedMirrorReset_MidDeleteFailure_RollsBackAllRowsAndEvidenceFile()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"shared-reset-failure-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "preserve-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-1.4\nshared reset rollback evidence"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "RESET-FAILURE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            db.Customers.Add(new LocalCustomer
            {
                Id = customerId,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
                NameOriginal = "reset rollback customer",
                NameMatchKey = "reset rollback customer",
                TradeType = CustomerTradeTypes.Sales,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER block_shared_reset_customer_delete
                BEFORE DELETE ON Customers
                BEGIN
                    SELECT RAISE(ABORT, 'blocked shared reset customer delete');
                END;
                """);

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await Assert.ThrowsAnyAsync<Exception>(() =>
                service.ResetSharedMirrorCacheAsync());

            db.ChangeTracker.Clear();
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            Assert.True(await db.Customers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == customerId));
            Assert.True(File.Exists(evidencePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task SharedMirrorReset_PublicCallInsideAmbientTransaction_IsRejectedBeforeMutation()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new LocalCustomer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "ambient reset customer",
            NameMatchKey = "ambient reset customer",
            TradeType = CustomerTradeTypes.Sales,
            IsDirty = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ResetSharedMirrorCacheAsync());

            Assert.True(await db.Customers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == customerId));
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    [Fact]
    public async Task ServerPurge_ExternalJournalWithoutAmbientTransaction_IsRejectedBeforeMutation()
    {
        using var scope = new TemporaryDirectory();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new LocalCustomer
        {
            Id = customerId,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            NameOriginal = "external journal guard customer",
            NameMatchKey = "external journal guard customer",
            TradeType = CustomerTradeTypes.Sales,
            IsDeleted = true,
            IsDirty = false
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            CreateAdminSession());
        using var attachmentFiles = new AttachmentFileJournal(
            Path.Combine(scope.Path, "journals"),
            Path.Combine(scope.Path, "attachments"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ApplyServerPurgeRecycleBinEntryAsync(
                RecycleBinEntityKind.Customer,
                customerId,
                attachmentFiles));

        Assert.True(await db.Customers
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == customerId));
    }

    [Fact]
    public async Task SharedMirrorReset_RootLeaseContention_RollsBackRowsAndFiles()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"shared-reset-root-lease-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-root-lease-reset-evidence"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "RESET-ROOT-LEASE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var blocker = new AttachmentFileJournal(
                AppPaths.AttachmentFileJournalDir,
                AppPaths.AttachmentsDir);
            await blocker.StageWriteAsync(
                Path.Combine(evidenceDirectory, "lease-holder.pdf"),
                "%PDF-root-lease-holder"u8.ToArray());

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await Assert.ThrowsAsync<AttachmentFileJournalContentionException>(
                () => service.ResetSharedMirrorCacheAsync());

            db.ChangeTracker.Clear();
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            Assert.True(File.Exists(evidencePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task ServerPurge_RootLeaseContention_RollsBackRowAndFile()
    {
        var evidenceDirectory = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"server-purge-root-lease-{Guid.NewGuid():N}");
        var evidencePath = Path.Combine(evidenceDirectory, "receive-proof.pdf");
        Directory.CreateDirectory(evidenceDirectory);
        await File.WriteAllBytesAsync(
            evidencePath,
            "%PDF-root-lease-purge-evidence"u8.ToArray());

        try
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>()
                    .UseSqlite(connection)
                    .Options);
            await db.Database.EnsureCreatedAsync();

            var transferId = Guid.NewGuid();
            db.InventoryTransfers.Add(new LocalInventoryTransfer
            {
                Id = transferId,
                TransferNumber = "PURGE-ROOT-LEASE-001",
                TransferDate = DateOnly.FromDateTime(DateTime.Today),
                ReceiveEvidencePath = evidencePath,
                IsDeleted = true,
                IsDirty = false
            });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            using var blocker = new AttachmentFileJournal(
                AppPaths.AttachmentFileJournalDir,
                AppPaths.AttachmentsDir);
            await blocker.StageWriteAsync(
                Path.Combine(evidenceDirectory, "lease-holder.pdf"),
                "%PDF-root-lease-holder"u8.ToArray());

            var service = new LocalStateService(
                db,
                new OfficeAccessService(),
                new SyncRequestDispatcher(),
                CreateAdminSession());

            await Assert.ThrowsAsync<AttachmentFileJournalContentionException>(
                () => service.ApplyServerPurgeRecycleBinEntryAsync(
                    RecycleBinEntityKind.InventoryTransfer,
                    transferId));

            db.ChangeTracker.Clear();
            Assert.True(await db.InventoryTransfers
                .IgnoreQueryFilters()
                .AnyAsync(current => current.Id == transferId));
            Assert.True(File.Exists(evidencePath));
        }
        finally
        {
            try
            {
                if (Directory.Exists(evidenceDirectory))
                    Directory.Delete(evidenceDirectory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup in the isolated test app root.
            }
        }
    }

    [Fact]
    public async Task SharedMirrorReset_OuterTransactionRollback_PreservesAttachmentAndInventoryEvidence()
    {
        var attachmentPath = Path.Combine(
            AppPaths.TransactionAttachmentsDir,
            $"shared-reset-rollback-{Guid.NewGuid():N}",
            "existing.pdf");
        var inventoryEvidencePath = Path.Combine(
            AppPaths.AttachmentsDir,
            "inventory",
            $"shared-reset-rollback-{Guid.NewGuid():N}",
            "receive-proof.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(attachmentPath)!);
        Directory.CreateDirectory(
            Path.GetDirectoryName(inventoryEvidencePath)!);
        await File.WriteAllBytesAsync(attachmentPath, "%PDF-existing"u8.ToArray());
        await File.WriteAllBytesAsync(
            inventoryEvidencePath,
            "%PDF-existing-inventory-evidence"u8.ToArray());

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(
            new DbContextOptionsBuilder<LocalDbContext>()
                .UseSqlite(connection)
                .Options);
        await db.Database.EnsureCreatedAsync();

        var transactionId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var transferId = Guid.NewGuid();
        db.Transactions.Add(new LocalTransaction
        {
            Id = transactionId,
            CustomerId = Guid.NewGuid(),
            TransactionKind = PaymentFlowConstants.TransactionKindReceipt,
            IsDirty = false
        });
        db.TransactionAttachments.Add(new LocalTransactionAttachment
        {
            Id = attachmentId,
            TransactionId = transactionId,
            FileName = "existing.pdf",
            StoredFileName = "existing.pdf",
            StoredPath = attachmentPath,
            MimeType = "application/pdf",
            FileSize = new FileInfo(attachmentPath).Length,
            IsDirty = false
        });
        db.InventoryTransfers.Add(new LocalInventoryTransfer
        {
            Id = transferId,
            TransferNumber = "RESET-ROLLBACK-EVIDENCE-001",
            TransferDate = DateOnly.FromDateTime(DateTime.Today),
            ReceiveEvidencePath = inventoryEvidencePath,
            IsDirty = false
        });
        await db.SaveChangesAsync();

        var service = new LocalStateService(
            db,
            new OfficeAccessService(),
            new SyncRequestDispatcher(),
            new SessionState());
        await using var transaction = await db.Database.BeginTransactionAsync();
        using var attachmentFiles = new AttachmentFileJournal(
            AppPaths.AttachmentFileJournalDir,
            AppPaths.AttachmentsDir);

        await service.ResetSharedMirrorCacheWithAttachmentJournalAsync(
            attachmentFiles);

        Assert.True(File.Exists(attachmentPath));
        Assert.True(File.Exists(inventoryEvidencePath));
        Assert.False(await db.TransactionAttachments
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == attachmentId));
        Assert.False(await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == transferId));

        await transaction.RollbackAsync();
        attachmentFiles.Rollback();
        db.ChangeTracker.Clear();

        Assert.True(File.Exists(attachmentPath));
        Assert.True(File.Exists(inventoryEvidencePath));
        Assert.True(await db.TransactionAttachments
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == attachmentId));
        Assert.True(await db.InventoryTransfers
            .IgnoreQueryFilters()
            .AnyAsync(current => current.Id == transferId));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"georaeplan-attachment-atomicity-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Best-effort test cleanup.
            }
        }
    }

    private static void ReleaseJournalLeaseToSimulateProcessExit(
        AttachmentFileJournal journal)
    {
        foreach (var fieldName in new[]
                 {
                     "_activeLease",
                     "_rootMutationLease"
                 })
        {
            var leaseField = typeof(AttachmentFileJournal).GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);
            Assert.NotNull(leaseField);
            var lease = Assert.IsAssignableFrom<IDisposable>(
                leaseField!.GetValue(journal));
            lease.Dispose();
        }
    }

    private static (
        long Length,
        string Sha256,
        string VolumeSerialNumber,
        string FileId)
        ReadAttachmentFileIdentityForTesting(string path)
    {
        var method = typeof(AttachmentFileJournal).GetMethod(
            "TryReadFileIdentity",
            System.Reflection.BindingFlags.Static |
            System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        var snapshot = method!.Invoke(null, [path]);
        Assert.NotNull(snapshot);
        var snapshotType = snapshot!.GetType();

        T ReadProperty<T>(string propertyName)
        {
            var property = snapshotType.GetProperty(propertyName);
            Assert.NotNull(property);
            return Assert.IsType<T>(property!.GetValue(snapshot));
        }

        return (
            ReadProperty<long>("Length"),
            ReadProperty<string>("Sha256"),
            ReadProperty<string>("VolumeSerialNumber"),
            ReadProperty<string>("FileId"));
    }

    private static void CreateDirectoryJunction(
        string junctionPath,
        string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(junctionPath, targetPath);
            return;
        }

        var startInfo = new ProcessStartInfo(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("mklink");
        startInfo.ArgumentList.Add("/J");
        startInfo.ArgumentList.Add(junctionPath);
        startInfo.ArgumentList.Add(targetPath);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                "Could not start the junction creation process.");
        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Could not create a test junction. {standardOutput} {standardError}");
        }
    }

    private static void DeleteDirectoryLink(string linkPath)
    {
        try
        {
            if (Directory.Exists(linkPath))
                Directory.Delete(linkPath);
        }
        catch
        {
            // Best-effort cleanup of the isolated test link.
        }
    }

    private static HashSet<string> CaptureAttachmentJournalDirectories()
    {
        if (!Directory.Exists(AppPaths.AttachmentFileJournalDir))
        {
            return new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);
        }

        return Directory.EnumerateDirectories(
                AppPaths.AttachmentFileJournalDir,
                "attachment-files-*",
                SearchOption.TopDirectoryOnly)
            .Select(Path.GetFullPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ThrowOnSaveChangesInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<InterceptionResult<int>>(
              new InvalidOperationException("simulated database save failure"));
    }

    private sealed class ThrowAfterCommitInterceptor : DbTransactionInterceptor
    {
        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
            => Task.FromException(
                new InvalidOperationException(
                    "simulated exception after database commit"));
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetSession(
            "attachment-atomicity-token",
            new UserSessionDto
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                TenantCode = TenantScopeCatalog.UsenetGroup,
                OfficeCode = OfficeCodeCatalog.Usenet,
                ScopeType = TenantScopeCatalog.ScopeAdmin
            });
        return session;
    }

    private static string FindRepositoryRoot(
        [CallerFilePath] string sourceFilePath = "")
    {
        var directory = new DirectoryInfo(
            Path.GetDirectoryName(sourceFilePath) ?? AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, ".git")) &&
                Directory.Exists(Path.Combine(directory.FullName, "Desktop")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
