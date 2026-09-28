using System.Text.Json;
using System.Reflection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class PaymentSyncRepairIdempotencyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletedInvoicePayment_RepairsOnceAndPreservesPendingTombstoneOnRetry(bool alreadyDeleted)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username = "repair-test", Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.Itworld, OfficeCode = OfficeCodeCatalog.Itworld,
            ScopeType = TenantScopeCatalog.ScopeAdmin });
        var customer = new LocalCustomer { Id = Guid.NewGuid(), NameOriginal = "Repair fixture",
            TenantCode = TenantScopeCatalog.Itworld, OfficeCode = OfficeCodeCatalog.Itworld,
            ResponsibleOfficeCode = OfficeCodeCatalog.Itworld };
        var invoice = new LocalInvoice { Id = Guid.NewGuid(), CustomerId = customer.Id, IsDeleted = true,
            TenantCode = customer.TenantCode, OfficeCode = customer.OfficeCode,
            ResponsibleOfficeCode = customer.ResponsibleOfficeCode, Revision = 111 };
        var payment = new LocalPayment { Id = Guid.NewGuid(), InvoiceId = invoice.Id, Amount = 55_000m,
            PaymentDate = new DateOnly(2026, 9, 22), IsDeleted = alreadyDeleted, IsDirty = true,
            Revision = 112, UpdatedAtUtc = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) };
        db.AddRange(customer, invoice, payment);
        await db.SaveChangesAsync();
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var original = JsonSerializer.Serialize(LocalMappings.ToDto(payment));
        var originalMutation = StampPayment(payment);
        var first = await local.RepairDirtyPaymentsForSyncAsync(session);
        Assert.Equal(alreadyDeleted ? 0 : 1, first.MarkedDeletedMissingInvoiceCount);
        Assert.True(payment.IsDeleted);
        Assert.True(payment.IsDirty); // Pending deletion still has to reach the server.
        Assert.Equal(112, payment.Revision);
        Assert.Equal(55_000m, payment.Amount);
        var repaired = JsonSerializer.Serialize(LocalMappings.ToDto(payment));
        var repairedMutation = StampPayment(payment);
        if (alreadyDeleted)
        {
            Assert.Equal(original, repaired);
            Assert.Equal(originalMutation, repairedMutation);
        }
        var second = await local.RepairDirtyPaymentsForSyncAsync(session);
        Assert.Equal(0, second.MarkedDeletedMissingInvoiceCount);
        Assert.Equal(repaired, JsonSerializer.Serialize(LocalMappings.ToDto(payment)));
        Assert.Equal(repairedMutation, StampPayment(payment));
        Assert.False(db.ChangeTracker.HasChanges());
    }

    private static string StampPayment(LocalPayment payment)
    {
        var request = new SyncPushRequest { DeviceId = "payment-repair-retry", Payments = [LocalMappings.ToDto(payment)] };
        var stamp = typeof(SyncService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(m => m.Name == "StampOutgoingMutations" && !m.IsGenericMethodDefinition && m.GetParameters().Length == 3);
        stamp.Invoke(null, [request, request.DeviceId, TenantScopeCatalog.Itworld]);
        return Assert.Single(request.Payments).MutationId;
    }
}
