using System.Collections;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingHistoryDeleteBatchTests
{
    [Fact]
    public async Task DeleteHistoryLookups_BatchInvoiceLinkedTransactionsAndAttachmentStatuses()
    {
        PrepareAppRoot("georaeplan-rental-delete-history-batch");

        try
        {
            await using var db = new LocalDbContext();
            await db.Database.EnsureDeletedAsync();
            await db.Database.EnsureCreatedAsync();

            var profileId = Guid.Parse("b9100000-1111-4444-8888-000000000001");
            var runId = Guid.Parse("b9200000-1111-4444-8888-000000000001");
            var invoiceIds = Enumerable.Range(0, 650)
                .Select(index => Guid.Parse($"b9300000-1111-4444-8888-{index + 1:000000000000}"))
                .ToList();
            var transactions = new List<LocalTransaction>();
            var directTransactionId = Guid.Parse("b9400000-1111-4444-8888-000000000001");
            var directTransaction = CreateTransaction(
                directTransactionId,
                linkedInvoiceId: invoiceIds[0],
                linkedProfileId: profileId,
                linkedRunId: runId,
                transactionDate: new DateOnly(2081, 1, 1),
                revision: 10);
            transactions.Add(directTransaction);

            transactions.AddRange(invoiceIds.Skip(1).Select((invoiceId, index) => CreateTransaction(
                Guid.Parse($"b9400000-2222-4444-8888-{index + 2:000000000000}"),
                linkedInvoiceId: invoiceId,
                linkedProfileId: null,
                linkedRunId: null,
                transactionDate: new DateOnly(2081, 1, 1).AddDays(index + 1),
                revision: 11 + index)));

            db.Transactions.AddRange(transactions);
            db.TransactionAttachments.AddRange(transactions.Select((transaction, index) => new LocalTransactionAttachment
            {
                Id = Guid.Parse($"b9500000-1111-4444-8888-{index + 1:000000000000}"),
                TransactionId = transaction.Id,
                AttachmentType = "receipt",
                FileName = $"receipt-{index + 1}.txt",
                StoredFileName = $"receipt-{index + 1}.txt",
                VerificationStatus = index % 2 == 0 ? "미확인" : "대기",
                IsDeleted = false,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            }));
            db.TransactionAttachments.Add(new LocalTransactionAttachment
            {
                Id = Guid.Parse("b9500000-9999-4444-8888-000000000001"),
                TransactionId = directTransactionId,
                AttachmentType = "receipt",
                FileName = "deleted-verified.txt",
                StoredFileName = "deleted-verified.txt",
                VerificationStatus = "확인완료",
                IsDeleted = true,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var service = new RentalStateService(db);
            var linkedTransactions = await InvokeLinkedTransactionsAsync(service, profileId, runId, invoiceIds);

            Assert.Equal(650, linkedTransactions.Count);
            Assert.Equal(directTransactionId, GetProperty<Guid>(linkedTransactions[0], "Id"));
            Assert.Equal(10, GetProperty<long>(linkedTransactions[0], "Revision"));
            Assert.Equal(new DateOnly(2081, 1, 1), GetProperty<DateOnly>(linkedTransactions[0], "TransactionDate"));
            Assert.Equal(650, linkedTransactions.Select(row => GetProperty<Guid>(row, "Id")).Distinct().Count());

            var transactionIds = linkedTransactions.Select(row => GetProperty<Guid>(row, "Id")).ToList();
            var attachmentStatuses = await InvokeAttachmentStatusesAsync(service, transactionIds);

            Assert.Equal(650, attachmentStatuses.Count);
            Assert.DoesNotContain(attachmentStatuses, status => string.Equals(status, "확인완료", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", null);
            SqliteConnection.ClearAllPools();
        }
    }

    private static LocalTransaction CreateTransaction(
        Guid id,
        Guid? linkedInvoiceId,
        Guid? linkedProfileId,
        Guid? linkedRunId,
        DateOnly transactionDate,
        long revision)
        => new()
        {
            Id = id,
            TenantCode = TenantScopeCatalog.UsenetGroup,
            OfficeCode = OfficeCodeCatalog.Usenet,
            ResponsibleOfficeCode = OfficeCodeCatalog.Usenet,
            TransactionDate = transactionDate,
            TransactionKind = PaymentFlowConstants.TransactionKindRentalReceipt,
            LinkedInvoiceId = linkedInvoiceId,
            LinkedRentalBillingProfileId = linkedProfileId,
            LinkedRentalBillingRunId = linkedRunId,
            ReceiptTotal = 10_000m,
            BankReceipt = 10_000m,
            SettlementAmount = 10_000m,
            Revision = revision,
            IsDeleted = false,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };

    private static async Task<List<object>> InvokeLinkedTransactionsAsync(
        RentalStateService service,
        Guid profileId,
        Guid runId,
        IReadOnlyCollection<Guid> invoiceIds)
    {
        var method = typeof(RentalStateService).GetMethod(
            "LoadBillingHistoryDeleteTransactionsAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            service,
            [profileId, runId, invoiceIds, CancellationToken.None]));
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return Assert.IsAssignableFrom<IEnumerable>(result).Cast<object>().ToList();
    }

    private static async Task<List<string>> InvokeAttachmentStatusesAsync(
        RentalStateService service,
        IReadOnlyCollection<Guid> transactionIds)
    {
        var method = typeof(RentalStateService).GetMethod(
            "LoadBillingHistoryDeleteAttachmentStatusesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = Assert.IsAssignableFrom<Task>(method!.Invoke(
            service,
            [transactionIds, CancellationToken.None]));
        await task;
        var result = task.GetType().GetProperty("Result")!.GetValue(task);
        return Assert.IsAssignableFrom<IEnumerable>(result).Cast<string>().ToList();
    }

    private static T GetProperty<T>(object source, string propertyName)
        => Assert.IsType<T>(source.GetType().GetProperty(propertyName)!.GetValue(source));

    private static void PrepareAppRoot(string prefix)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", tempRoot);
    }
}
