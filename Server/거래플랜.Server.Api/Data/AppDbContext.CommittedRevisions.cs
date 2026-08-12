using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Server.Api.Domain;

namespace 거래플랜.Server.Api.Data;

public sealed partial class AppDbContext
{
    private const int SyncRevisionStateId = 1;

    public long GetCommittedRevision()
    {
        if (!Database.IsRelational())
            return _revisionClock.Current;

        var connection = Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            connection.Open();

        try
        {
            using var command = CreateRevisionCommand(
                connection,
                """SELECT "CurrentRevision" FROM "SyncRevisionStates" WHERE "Id" = 1;""");
            var result = command.ExecuteScalar();
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
        finally
        {
            if (closeConnection)
                connection.Close();
        }
    }

    public async Task<long> GetCommittedRevisionAsync(CancellationToken cancellationToken = default)
    {
        if (!Database.IsRelational())
            return _revisionClock.Current;

        var connection = Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await connection.OpenAsync(cancellationToken);

        try
        {
            await using var command = CreateRevisionCommand(
                connection,
                """SELECT "CurrentRevision" FROM "SyncRevisionStates" WHERE "Id" = 1;""");
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result is null or DBNull ? 0 : Convert.ToInt64(result);
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }

    internal async Task AdvanceCommittedRevisionFloorAsync(
        long revisionFloor,
        CancellationToken cancellationToken = default)
    {
        if (!Database.IsRelational())
        {
            _revisionClock.Initialize(revisionFloor);
            return;
        }

        var connection = Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
            await connection.OpenAsync(cancellationToken);

        try
        {
            var greatest = Database.IsSqlite() ? "MAX" : "GREATEST";
            var sql =
                $"""
                 INSERT INTO "SyncRevisionStates" ("Id", "CurrentRevision")
                 VALUES ({SyncRevisionStateId}, @revisionFloor)
                 ON CONFLICT ("Id") DO UPDATE
                 SET "CurrentRevision" = {greatest}("SyncRevisionStates"."CurrentRevision", @revisionFloor);
                 """;
            await using var command = CreateRevisionCommand(connection, sql);
            AddParameter(command, "@revisionFloor", revisionFloor);
            await command.ExecuteNonQueryAsync(cancellationToken);
            _revisionClock.Initialize(revisionFloor);
        }
        finally
        {
            if (closeConnection)
                await connection.CloseAsync();
        }
    }

    private int SaveChangesWithCommittedRevisions(bool acceptAllChangesOnSuccess)
    {
        var revisionTargets = FindRevisionTargets();
        if (revisionTargets.Count == 0 || !Database.IsRelational())
        {
            PrepareTrackedEntityState();
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }

        var ownsTransaction = Database.CurrentTransaction is null;
        using var transaction = ownsTransaction ? Database.BeginTransaction() : null;
        try
        {
            var revisions = ReserveCommittedRevisions(revisionTargets);
            PrepareTrackedEntityState(revisions);
            var saved = base.SaveChanges(acceptAllChangesOnSuccess);
            if (ownsTransaction)
                transaction!.Commit();
            return saved;
        }
        catch
        {
            if (ownsTransaction)
                transaction?.Rollback();
            throw;
        }
    }

    private async Task<int> SaveChangesWithCommittedRevisionsAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        var revisionTargets = FindRevisionTargets();
        if (revisionTargets.Count == 0 || !Database.IsRelational())
        {
            PrepareTrackedEntityState();
            return await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken);
        }

        var ownsTransaction = Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction
            ? await Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var revisions = await ReserveCommittedRevisionsAsync(
                revisionTargets,
                cancellationToken);
            PrepareTrackedEntityState(revisions);
            var saved = await base.SaveChangesAsync(
                acceptAllChangesOnSuccess,
                cancellationToken);
            if (ownsTransaction)
                await transaction!.CommitAsync(cancellationToken);
            return saved;
        }
        catch
        {
            if (ownsTransaction && transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private List<EntityEntry> FindRevisionTargets()
        => ChangeTracker.Entries()
            .Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted &&
                entry.Entity is TrackedEntity or ItemWarehouseStock)
            .ToList();

    private IReadOnlyDictionary<object, long> ReserveCommittedRevisions(
        IReadOnlyList<EntityEntry> revisionTargets)
    {
        var connection = Database.GetDbConnection();
        using var command = CreateRevisionReservationCommand(
            connection,
            revisionTargets.Count);
        var result = command.ExecuteScalar();
        var lastRevision = Convert.ToInt64(result);
        _revisionClock.Initialize(lastRevision);
        return BuildReservedRevisionMap(revisionTargets, lastRevision);
    }

    private async Task<IReadOnlyDictionary<object, long>> ReserveCommittedRevisionsAsync(
        IReadOnlyList<EntityEntry> revisionTargets,
        CancellationToken cancellationToken)
    {
        var connection = Database.GetDbConnection();
        await using var command = CreateRevisionReservationCommand(
            connection,
            revisionTargets.Count);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        var lastRevision = Convert.ToInt64(result);
        _revisionClock.Initialize(lastRevision);
        return BuildReservedRevisionMap(revisionTargets, lastRevision);
    }

    private DbCommand CreateRevisionReservationCommand(
        DbConnection connection,
        int revisionCount)
    {
        var candidateLastRevision =
            checked(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + revisionCount - 1L);
        var greatest = Database.IsSqlite() ? "MAX" : "GREATEST";
        var sql =
            $"""
             INSERT INTO "SyncRevisionStates" ("Id", "CurrentRevision")
             VALUES ({SyncRevisionStateId}, @candidateLastRevision)
             ON CONFLICT ("Id") DO UPDATE
             SET "CurrentRevision" = {greatest}(
                 "SyncRevisionStates"."CurrentRevision" + @revisionCount,
                 @candidateLastRevision)
             RETURNING "CurrentRevision";
             """;
        var command = CreateRevisionCommand(connection, sql);
        AddParameter(command, "@revisionCount", revisionCount);
        AddParameter(command, "@candidateLastRevision", candidateLastRevision);
        return command;
    }

    private DbCommand CreateRevisionCommand(DbConnection connection, string commandText)
    {
        var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Transaction = Database.CurrentTransaction?.GetDbTransaction();
        return command;
    }

    private static void AddParameter(
        DbCommand command,
        string name,
        object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static IReadOnlyDictionary<object, long> BuildReservedRevisionMap(
        IReadOnlyList<EntityEntry> revisionTargets,
        long lastRevision)
    {
        var firstRevision = checked(lastRevision - revisionTargets.Count + 1L);
        var revisions = new Dictionary<object, long>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < revisionTargets.Count; index++)
            revisions.Add(revisionTargets[index].Entity, checked(firstRevision + index));
        return revisions;
    }
}
