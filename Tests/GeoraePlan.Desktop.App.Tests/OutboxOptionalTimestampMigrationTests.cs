using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using 거래플랜.Desktop.App.Data;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class OutboxOptionalTimestampMigrationTests
{
    public static IEnumerable<object?[]> Cases()
    {
        foreach(var status in new[]{"Prepared","Failed","Acknowledged"})
        foreach(var value in new string?[]{null,"","   ","2026-09-25T01:02:03Z","1970-01-01T00:00:00Z"})
            yield return [status,value];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task InitializePreservesAbsentOptionalEventsAndExistingReceipts(string status,string? value)
    {
        await using var connection=new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db=new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await LocalDbInitializer.InitializeAsync(db);
        var row=new LocalSyncOutboxEntry { EntityName="Invoice",EntityId=Guid.NewGuid(),Status=status,
            TenantCode="USENET_GROUP",OfficeCode="USENET",ExpectedRevision=123,AcceptedRevision=status=="Acknowledged"?456:0,
            MutationId=Guid.NewGuid().ToString("N"),DeviceId="migration-device",ErrorMessage=status=="Failed"?"preserved conflict":"" };
        db.SyncOutboxEntries.Add(row);await db.SaveChangesAsync();db.ChangeTracker.Clear();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE SyncOutboxEntries SET SentAtUtc={value}, AcknowledgedAtUtc={value}, AcceptedUpdatedAtUtc={value} WHERE MutationId={row.MutationId}");
        var before=await ReadRow(connection,row.MutationId);
        var expected=string.IsNullOrWhiteSpace(value)?null:value;
        for(var pass=0;pass<2;pass++)
        {
            await LocalDbInitializer.InitializeAsync(db);db.ChangeTracker.Clear();
            var after=await ReadRow(connection,row.MutationId);
            foreach(var field in new[]{"SentAtUtc","AcknowledgedAtUtc","AcceptedUpdatedAtUtc"})
            {
                Assert.Equal(expected,after[field]);
                after.Remove(field);
            }
            Assert.Equal(JsonSerializer.Serialize(before.Where(p=>!new[]{"SentAtUtc","AcknowledgedAtUtc","AcceptedUpdatedAtUtc"}.Contains(p.Key)).ToDictionary(p=>p.Key,p=>p.Value)),JsonSerializer.Serialize(after));
        }
    }

    private static async Task<Dictionary<string,object?>> ReadRow(SqliteConnection c,string mutationId)
    {
        using var command=c.CreateCommand();command.CommandText="SELECT * FROM SyncOutboxEntries WHERE MutationId=$id";command.Parameters.AddWithValue("$id",mutationId);
        await using var reader=await command.ExecuteReaderAsync();Assert.True(await reader.ReadAsync());
        return Enumerable.Range(0,reader.FieldCount).ToDictionary(reader.GetName,i=>reader.IsDBNull(i)?null:reader.GetValue(i));
    }
}
