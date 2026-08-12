using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

public sealed class LocalDbContext : DbContext
{
    private static readonly SemaphoreSlim RuntimeMutationGate = new(1, 1);
    private static readonly AsyncLocal<RuntimeMutationOwnerFrame?> RuntimeMutationOwner = new();
    private static readonly AsyncLocal<RuntimeMutationOwnerSuppression?> RuntimeMutationOwnerSuppressionContext = new();
    private static readonly AsyncLocal<RuntimeMutationExecutionFrame?> RuntimeMutationExecution = new();
    private static readonly AsyncLocal<RuntimeMutationTransactionFlow?> RuntimeMutationTransaction = new();
    private static readonly ConcurrentDictionary<string, long> RuntimeMutationEpochs = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, RuntimeMutationGuardFault> RuntimeMutationGuardFaults = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<object, RuntimeMutationEntityStamp> RuntimeMutationEntityStamps = new();
    private static RuntimeMutationGuardFault? RuntimeMutationGlobalFault;
    private const string RuntimeMutationCleanupFailureDataKey = "RuntimeMutationProviderCleanupFailure";
    private string? _runtimeMutationKey;
    private long? _observedRuntimeMutationEpoch;
    private int _runtimeMutationOperationLeaseDepth;
    private int _runtimeMutationTransactionLeaseDepth;
    private int _runtimeMutationTransactionStartPending;
    private readonly object _runtimeMutationActiveTransactionGate = new();
    private RuntimeMutationDbContextTransaction? _runtimeMutationActiveTransaction;
    private readonly HashSet<RuntimeMutationDbContextTransaction> _runtimeMutationTrackedTransactions = [];

    public LocalDbContext()
    {
        ObserveRuntimeMutationEpochWhenTrackingStarts();
    }

    public LocalDbContext(DbContextOptions<LocalDbContext> options)
        : base(options)
    {
        ObserveRuntimeMutationEpochWhenTrackingStarts();
    }

    public override void Dispose()
    {
        Exception? primaryFailure = null;
        foreach (var transaction in GetTrackedRuntimeMutationTransactions())
        {
            try
            {
                transaction.Dispose();
            }
            catch (RuntimeMutationAsyncCompletionRequiredException)
            {
                throw;
            }
            catch (Exception exception)
            {
                if (primaryFailure is null)
                    primaryFailure = exception;
                else
                    AttachCleanupFailure(primaryFailure, exception);
            }
        }

        try
        {
            base.Dispose();
        }
        catch (Exception exception)
        {
            if (primaryFailure is null)
                throw;
            AttachCleanupFailure(primaryFailure, exception);
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    public override ValueTask DisposeAsync()
    {
        var transactions = GetTrackedRuntimeMutationTransactions();
        foreach (var transaction in transactions)
            transaction.DetachRootTransactionFlow();
        return DisposeContextAsync(transactions);
    }

    private async ValueTask DisposeContextAsync(
        IReadOnlyList<RuntimeMutationDbContextTransaction> transactions)
    {
        Exception? primaryFailure = null;
        foreach (var transaction in transactions)
        {
            try
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                if (primaryFailure is null)
                    primaryFailure = exception;
                else
                    AttachCleanupFailure(primaryFailure, exception);
            }
        }

        try
        {
            await base.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (primaryFailure is null)
                throw;
            AttachCleanupFailure(primaryFailure, exception);
        }

        if (primaryFailure is not null)
            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    public override int SaveChanges()
        => SaveChanges(acceptAllChangesOnSuccess: true);

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        ThrowIfRuntimeMutationOwnerIsSuppressed();
        ThrowIfCrossContextRuntimeMutationIsActive();
        if (TryAcquireRuntimeMutationTransactionBorrow(out var transactionBorrow))
        {
            using (transactionBorrow)
                return SaveChangesCore(acceptAllChangesOnSuccess);
        }
        ThrowIfOwnedRuntimeMutationTransactionIsUnavailable();
        if (TryAcquireRuntimeMutationExecutionBorrow(out var executionBorrow))
        {
            using (executionBorrow)
                return SaveChangesCore(acceptAllChangesOnSuccess);
        }

        var operationStartEpoch = CaptureRuntimeMutationEpoch();
        if (TryAcquireRuntimeMutationOwnerBorrow(out var ownerBorrow))
        {
            using (ownerBorrow)
            {
                ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
                return SaveChangesWithRuntimeMutationOperationLease(
                    acceptAllChangesOnSuccess,
                    ownerBorrow);
            }
        }

        RuntimeMutationGate.Wait();
        using var gateLease = new RuntimeMutationGateLease();
        ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
        return SaveChangesWithRuntimeMutationOperationLease(
            acceptAllChangesOnSuccess,
            gateLease);
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
        => SaveChangesAsync(
            acceptAllChangesOnSuccess: true,
            cancellationToken);

    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken = default)
    {
        ThrowIfRuntimeMutationOwnerIsSuppressed();
        ThrowIfCrossContextRuntimeMutationIsActive();
        if (TryAcquireRuntimeMutationTransactionBorrow(out var transactionBorrow))
        {
            using (transactionBorrow)
                return await SaveChangesCoreAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        ThrowIfOwnedRuntimeMutationTransactionIsUnavailable();
        if (TryAcquireRuntimeMutationExecutionBorrow(out var executionBorrow))
        {
            using (executionBorrow)
                return await SaveChangesCoreAsync(acceptAllChangesOnSuccess, cancellationToken);
        }

        var operationStartEpoch = CaptureRuntimeMutationEpoch();
        var ownerBorrow = await TryAcquireRuntimeMutationOwnerBorrowAsync(cancellationToken);
        if (ownerBorrow is not null)
        {
            using (ownerBorrow)
            {
                ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
                return await SaveChangesWithRuntimeMutationOperationLeaseAsync(
                    acceptAllChangesOnSuccess,
                    ownerBorrow,
                    cancellationToken);
            }
        }

        await RuntimeMutationGate.WaitAsync(cancellationToken);
        using var gateLease = new RuntimeMutationGateLease();
        ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
        return await SaveChangesWithRuntimeMutationOperationLeaseAsync(
            acceptAllChangesOnSuccess,
            gateLease,
            cancellationToken);
    }

    internal static async Task<IAsyncDisposable> AcquireRuntimeMutationGateAsync(
        CancellationToken cancellationToken)
    {
        await RuntimeMutationGate.WaitAsync(cancellationToken);
        return new RuntimeMutationGateLease();
    }

    internal static IAsyncDisposable EnterRuntimeMutationGateOwnerScope(
        IAsyncDisposable gateLease)
    {
        if (gateLease is not IRuntimeMutationGateHandle handle)
        {
            throw new ArgumentException(
                "The runtime mutation owner scope requires a runtime mutation gate lease.",
                nameof(gateLease));
        }

        return EnterRuntimeMutationGateOwnerScope(handle);
    }

    internal static IDisposable SuppressRuntimeMutationOwnerForCallback()
    {
        if (!HasActiveRuntimeMutationOwner())
            return NoopRuntimeMutationGateLease.Instance;

        var previous = RuntimeMutationOwnerSuppressionContext.Value;
        var suppression = new RuntimeMutationOwnerSuppression();
        RuntimeMutationOwnerSuppressionContext.Value = suppression;
        return new RuntimeMutationOwnerSuppressionScope(suppression, previous);
    }

    internal Task<IDbContextTransaction> BeginRuntimeMutationTransactionAsync(
        CancellationToken cancellationToken = default)
        => StartRuntimeMutationTransactionAsync(
            isolationLevel: null,
            cancellationToken);

    internal Task<IDbContextTransaction> BeginRuntimeMutationTransactionAsync(
        IsolationLevel isolationLevel,
        CancellationToken cancellationToken = default)
        => StartRuntimeMutationTransactionAsync(
            isolationLevel,
            cancellationToken);

    internal async Task<T> ExecuteRuntimeMutationCommandAsync<T>(
        Func<Task<T>> command,
        CancellationToken cancellationToken = default)
        => await ExecuteRuntimeMutationOperationAsync(command, cancellationToken);

    internal async Task ExecuteRuntimeMutationOperationAsync(
        Func<Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        await ExecuteRuntimeMutationOperationAsync(
            async () =>
            {
                await operation();
                return true;
            },
            cancellationToken);
    }

    internal async Task<T> ExecuteRuntimeMutationOperationAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ThrowIfRuntimeMutationOwnerIsSuppressed();
        if (TryAcquireRuntimeMutationTransactionBorrow(out var transactionBorrow))
        {
            using (transactionBorrow)
                return await operation();
        }
        ThrowIfOwnedRuntimeMutationTransactionIsUnavailable();
        if (OwnsRuntimeMutationExecutionInCurrentFlow())
        {
            throw new InvalidOperationException(
                "Nested runtime mutation operations are not supported. Execute the nested commands inside the existing operation instead.");
        }

        ThrowIfCrossContextRuntimeMutationIsActive();
        var operationStartEpoch = CaptureRuntimeMutationEpoch();
        var ownerBorrow = await TryAcquireRuntimeMutationOwnerBorrowAsync(cancellationToken);
        if (ownerBorrow is not null)
        {
            using (ownerBorrow)
            {
                ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
                return await ExecuteWithRuntimeMutationOperationLeaseAsync(
                    operation,
                    ownerBorrow);
            }
        }

        await RuntimeMutationGate.WaitAsync(cancellationToken);
        using var gateLease = new RuntimeMutationGateLease();
        ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
        return await ExecuteWithRuntimeMutationOperationLeaseAsync(
            operation,
            gateLease);
    }

    internal IDisposable AcquireRuntimeMutationCommandGate()
    {
        ThrowIfRuntimeMutationOwnerIsSuppressed();
        ThrowIfCrossContextRuntimeMutationIsActive();
        if (TryAcquireRuntimeMutationTransactionBorrow(out var transactionBorrow))
            return transactionBorrow;
        ThrowIfOwnedRuntimeMutationTransactionIsUnavailable();
        if (TryAcquireRuntimeMutationExecutionBorrow(out var executionBorrow))
            return executionBorrow;

        var operationStartEpoch = CaptureRuntimeMutationEpoch();
        if (TryAcquireRuntimeMutationOwnerBorrow(out var ownerBorrow))
        {
            try
            {
                ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
                return ownerBorrow;
            }
            catch
            {
                ownerBorrow.Dispose();
                throw;
            }
        }

        RuntimeMutationGate.Wait();
        var gateLease = new RuntimeMutationGateLease();
        try
        {
            ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
            return gateLease;
        }
        catch
        {
            gateLease.Dispose();
            throw;
        }
    }

    internal async Task<IAsyncDisposable> AcquireRuntimeMutationCommandGateAsync(
        CancellationToken cancellationToken)
    {
        ThrowIfRuntimeMutationOwnerIsSuppressed();
        ThrowIfCrossContextRuntimeMutationIsActive();
        if (TryAcquireRuntimeMutationTransactionBorrow(out var transactionBorrow))
            return transactionBorrow;
        ThrowIfOwnedRuntimeMutationTransactionIsUnavailable();
        if (TryAcquireRuntimeMutationExecutionBorrow(out var executionBorrow))
            return executionBorrow;

        var operationStartEpoch = CaptureRuntimeMutationEpoch();
        var ownerBorrow = await TryAcquireRuntimeMutationOwnerBorrowAsync(cancellationToken);
        if (ownerBorrow is not null)
        {
            try
            {
                ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
                return ownerBorrow;
            }
            catch
            {
                await ownerBorrow.DisposeAsync();
                throw;
            }
        }

        await RuntimeMutationGate.WaitAsync(cancellationToken);
        var gateLease = new RuntimeMutationGateLease();
        try
        {
            ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
            return gateLease;
        }
        catch
        {
            gateLease.Dispose();
            throw;
        }
    }

    internal long AdvanceRuntimeMutationEpoch()
    {
        var key = GetRuntimeMutationKey();
        return RuntimeMutationEpochs.AddOrUpdate(key, 1, static (_, current) => checked(current + 1));
    }

    internal void AcceptCurrentRuntimeMutationEpoch()
    {
        var key = GetRuntimeMutationKey();
        var epoch = RuntimeMutationEpochs.GetOrAdd(key, 0);
        _observedRuntimeMutationEpoch = epoch;
        foreach (var entry in ChangeTracker.Entries())
            StampRuntimeMutationEntity(entry.Entity, key, epoch);
    }

    internal void ObserveCurrentRuntimeMutationEpoch()
        => EnsureRuntimeMutationEpochObserved();

    internal object StampMaterializedRuntimeMutationEntity(object entity)
    {
        var key = GetRuntimeMutationKey();
        var epoch = EnsureRuntimeMutationEpochObserved();
        StampRuntimeMutationEntity(entity, key, epoch);
        return entity;
    }

    private int SaveChangesWithRuntimeMutationOperationLease(
        bool acceptAllChangesOnSuccess,
        IRuntimeMutationGateHandle gateHandle)
    {
        Interlocked.Increment(ref _runtimeMutationOperationLeaseDepth);
        using var executionScope = EnterRuntimeMutationExecutionScope(this, gateHandle);
        try
        {
            return SaveChangesCore(acceptAllChangesOnSuccess);
        }
        finally
        {
            ReleaseRuntimeMutationOperationLease();
        }
    }

    private async Task<int> SaveChangesWithRuntimeMutationOperationLeaseAsync(
        bool acceptAllChangesOnSuccess,
        IRuntimeMutationGateHandle gateHandle,
        CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runtimeMutationOperationLeaseDepth);
        await using var executionScope = EnterRuntimeMutationExecutionScope(this, gateHandle);
        try
        {
            return await SaveChangesCoreAsync(
                acceptAllChangesOnSuccess,
                cancellationToken);
        }
        finally
        {
            ReleaseRuntimeMutationOperationLease();
        }
    }

    private async Task<T> ExecuteWithRuntimeMutationOperationLeaseAsync<T>(
        Func<Task<T>> operation,
        IRuntimeMutationGateHandle gateHandle)
    {
        Interlocked.Increment(ref _runtimeMutationOperationLeaseDepth);
        await using var executionScope = EnterRuntimeMutationExecutionScope(this, gateHandle);
        try
        {
            return await operation();
        }
        finally
        {
            ReleaseRuntimeMutationOperationLease();
        }
    }

    private void ReleaseRuntimeMutationOperationLease()
    {
        var remainingDepth = Interlocked.Decrement(ref _runtimeMutationOperationLeaseDepth);
        if (remainingDepth >= 0)
            return;

        Interlocked.Exchange(ref _runtimeMutationOperationLeaseDepth, 0);
        throw new InvalidOperationException("Runtime mutation operation lease depth became invalid.");
    }

    private int SaveChangesCore(bool acceptAllChangesOnSuccess)
    {
        PreserveDirtyMarkersForModifiedSyncGraphs();
        var saved = base.SaveChanges(acceptAllChangesOnSuccess);
        AcceptCurrentRuntimeMutationEpoch();
        return saved;
    }

    private async Task<int> SaveChangesCoreAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        PreserveDirtyMarkersForModifiedSyncGraphs();
        var saved = await base.SaveChangesAsync(
            acceptAllChangesOnSuccess,
            cancellationToken);
        AcceptCurrentRuntimeMutationEpoch();
        return saved;
    }

    private long CaptureRuntimeMutationEpoch()
    {
        var key = GetRuntimeMutationKey();
        ThrowIfRuntimeMutationGuardIsFaulted(key);
        return RuntimeMutationEpochs.GetOrAdd(key, 0);
    }

    private void ThrowIfRuntimeMutationEpochIsStale(long? operationStartEpoch = null)
    {
        var key = GetRuntimeMutationKey();
        ThrowIfRuntimeMutationGuardIsFaulted(key);
        var currentEpoch = RuntimeMutationEpochs.GetOrAdd(key, 0);
        if (operationStartEpoch.HasValue && operationStartEpoch.Value != currentEpoch)
            throw CreateRuntimeMutationConcurrencyException();

        if (!_observedRuntimeMutationEpoch.HasValue)
        {
            _observedRuntimeMutationEpoch = currentEpoch;
            return;
        }

        if (_observedRuntimeMutationEpoch.Value != currentEpoch)
            throw CreateRuntimeMutationConcurrencyException();

        foreach (var entry in ChangeTracker.Entries())
        {
            if (RuntimeMutationEntityStamps.TryGetValue(entry.Entity, out var stamp) &&
                string.Equals(stamp.RuntimeMutationKey, key, StringComparison.OrdinalIgnoreCase) &&
                stamp.Epoch != currentEpoch)
            {
                throw CreateRuntimeMutationConcurrencyException();
            }
        }
    }

    private static void ThrowIfRuntimeMutationGuardIsFaulted(string key)
    {
        var fault = Volatile.Read(ref RuntimeMutationGlobalFault);
        if (fault is null && !RuntimeMutationGuardFaults.TryGetValue(key, out fault))
            return;

        throw new InvalidOperationException(
            "공급자 트랜잭션 종료를 확인할 수 없어 이 데이터베이스의 저장을 안전 정지했습니다. 애플리케이션을 다시 시작한 뒤 저장하세요.",
            fault.PrimaryFailure);
    }

    private void FaultRuntimeMutationGuard(
        Exception primaryFailure,
        Exception cleanupFailure)
    {
        var fault = new RuntimeMutationGuardFault(primaryFailure, cleanupFailure);
        try
        {
            RuntimeMutationGuardFaults.TryAdd(GetRuntimeMutationKey(), fault);
        }
        catch
        {
            Volatile.Write(ref RuntimeMutationGlobalFault, fault);
        }
    }

    private bool TryQuarantineRuntimeMutationConnection(out Exception? failure)
    {
        failure = null;
        try
        {
            Database.CloseConnection();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        ConnectionState state;
        try
        {
            state = Database.GetDbConnection().State;
        }
        catch (Exception exception)
        {
            failure = CombineCleanupFailures(failure, exception);
            return false;
        }

        if (state is ConnectionState.Closed or ConnectionState.Broken)
            return true;

        failure = CombineCleanupFailures(
            failure,
            new InvalidOperationException(
                $"Runtime mutation connection quarantine ended in state '{state}'."));
        return false;
    }

    private async Task<(bool Terminated, Exception? Failure)> TryQuarantineRuntimeMutationConnectionAsync()
    {
        Exception? failure = null;
        try
        {
            await Database.CloseConnectionAsync();
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        ConnectionState state;
        try
        {
            state = Database.GetDbConnection().State;
        }
        catch (Exception exception)
        {
            failure = CombineCleanupFailures(failure, exception);
            return (false, failure);
        }

        if (state is ConnectionState.Closed or ConnectionState.Broken)
            return (true, failure);

        failure = CombineCleanupFailures(
            failure,
            new InvalidOperationException(
                $"Runtime mutation connection quarantine ended in state '{state}'."));
        return (false, failure);
    }

    private static Exception CombineCleanupFailures(
        Exception? current,
        Exception next)
        => current is null
            ? next
            : new AggregateException(current, next);

    private static void AttachCleanupFailure(
        Exception primaryFailure,
        Exception? cleanupFailure)
    {
        if (cleanupFailure is null)
            return;

        try
        {
            var existing = primaryFailure.Data[RuntimeMutationCleanupFailureDataKey]
                as Exception;
            primaryFailure.Data[RuntimeMutationCleanupFailureDataKey] =
                existing is null
                    ? cleanupFailure
                    : CombineCleanupFailures(existing, cleanupFailure);
        }
        catch
        {
            // Preserve the primary transaction failure even if diagnostic attachment fails.
        }
    }

    private static void StampRuntimeMutationEntity(
        object entity,
        string runtimeMutationKey,
        long epoch)
    {
        RuntimeMutationEntityStamps.Remove(entity);
        RuntimeMutationEntityStamps.Add(
            entity,
            new RuntimeMutationEntityStamp(runtimeMutationKey, epoch));
    }

    private static DbUpdateConcurrencyException CreateRuntimeMutationConcurrencyException()
        => new(
            "서버 기준 데이터가 갱신되는 동안 다른 화면에서 시작된 저장이 감지되어 반영하지 않았습니다. 해당 화면을 닫고 다시 연 뒤 최신 데이터로 저장하세요.");

    private Task<IDbContextTransaction> StartRuntimeMutationTransactionAsync(
        IsolationLevel? isolationLevel,
        CancellationToken cancellationToken)
    {
        ThrowIfRuntimeMutationOwnerIsSuppressed();
        if (OwnsRuntimeMutationExecutionInCurrentFlow())
        {
            throw new InvalidOperationException(
                "A runtime mutation transaction cannot begin inside another runtime mutation operation. Begin the transaction before the operation instead.");
        }

        var activeTransaction = GetCurrentRuntimeMutationTransactionFlow();
        if (activeTransaction is not null)
        {
            var message = activeTransaction.IsActive
                ? ReferenceEquals(activeTransaction.Owner, this)
                    ? "Nested runtime mutation transactions are not supported. Reuse the current transaction instead."
                    : "A different LocalDbContext cannot mutate while the current logical flow owns a runtime mutation transaction."
                : "A captured runtime mutation transaction flow is already closed and cannot start another transaction.";
            throw new InvalidOperationException(message);
        }

        var transactionFlow = new RuntimeMutationTransactionFlow(
            this,
            activeTransaction);
        RuntimeMutationTransaction.Value = transactionFlow;
        return BeginRuntimeMutationTransactionCoreAsync(
            isolationLevel,
            transactionFlow,
            cancellationToken);
    }

    private async Task<IDbContextTransaction> BeginRuntimeMutationTransactionCoreAsync(
        IsolationLevel? isolationLevel,
        RuntimeMutationTransactionFlow transactionFlow,
        CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _runtimeMutationTransactionLeaseDepth) > 0 ||
            Interlocked.CompareExchange(
                ref _runtimeMutationTransactionStartPending,
                1,
                0) != 0)
        {
            await transactionFlow.CloseAndDrainAsync();
            RestoreRuntimeMutationTransactionFlow(transactionFlow);
            throw new InvalidOperationException(
                "Nested runtime mutation transactions are not supported. Reuse the current transaction instead.");
        }

        long operationStartEpoch = 0;
        IRuntimeMutationGateHandle? gateHandle = null;
        RuntimeMutationDbContextTransaction? runtimeTransaction = null;
        try
        {
            ThrowIfCrossContextRuntimeMutationIsActive();
            operationStartEpoch = CaptureRuntimeMutationEpoch();
            var ownerBorrow = await TryAcquireRuntimeMutationOwnerBorrowAsync(cancellationToken);
            if (ownerBorrow is not null)
            {
                gateHandle = ownerBorrow;
            }
            else
            {
                await RuntimeMutationGate.WaitAsync(cancellationToken);
                gateHandle = new RuntimeMutationGateLease();
            }

            ThrowIfRuntimeMutationEpochIsStale(operationStartEpoch);
            var transaction = isolationLevel.HasValue
                ? await Database.BeginTransactionAsync(isolationLevel.Value, cancellationToken)
                : await Database.BeginTransactionAsync(cancellationToken);

            runtimeTransaction = new RuntimeMutationDbContextTransaction(
                transaction,
                this,
                gateHandle,
                transactionFlow);
            Interlocked.Increment(ref _runtimeMutationTransactionLeaseDepth);
            RegisterActiveRuntimeMutationTransaction(runtimeTransaction);
            transactionFlow.AttachState(gateHandle.State);
            Volatile.Write(ref _runtimeMutationTransactionStartPending, 0);
            return runtimeTransaction;
        }
        catch (Exception primaryFailure)
        {
            await transactionFlow.CloseAndDrainAsync();
            Volatile.Write(ref _runtimeMutationTransactionStartPending, 0);
            if (runtimeTransaction is not null)
            {
                try
                {
                    await runtimeTransaction.DisposeAsync();
                }
                catch (Exception cleanupFailure)
                {
                    AttachCleanupFailure(primaryFailure, cleanupFailure);
                }
            }
            else
            {
                var quarantine = await TryQuarantineRuntimeMutationConnectionAsync();
                if (!quarantine.Terminated)
                {
                    FaultRuntimeMutationGuard(
                        primaryFailure,
                        quarantine.Failure ?? new InvalidOperationException(
                            "Runtime mutation provider transaction start cleanup could not be confirmed."));
                }
                AttachCleanupFailure(primaryFailure, quarantine.Failure);

                gateHandle?.Dispose();
                RestoreRuntimeMutationTransactionFlow(transactionFlow);
            }
            throw;
        }
    }

    private void RegisterActiveRuntimeMutationTransaction(
        RuntimeMutationDbContextTransaction transaction)
    {
        lock (_runtimeMutationActiveTransactionGate)
        {
            if (_runtimeMutationActiveTransaction is not null)
            {
                throw new InvalidOperationException(
                    "A runtime mutation transaction is already registered on this LocalDbContext.");
            }
            _runtimeMutationActiveTransaction = transaction;
            _runtimeMutationTrackedTransactions.Add(transaction);
        }
    }

    private IReadOnlyList<RuntimeMutationDbContextTransaction> GetTrackedRuntimeMutationTransactions()
    {
        lock (_runtimeMutationActiveTransactionGate)
            return _runtimeMutationTrackedTransactions.ToArray();
    }

    private void UnregisterActiveRuntimeMutationTransaction(
        RuntimeMutationDbContextTransaction transaction)
    {
        lock (_runtimeMutationActiveTransactionGate)
        {
            if (ReferenceEquals(_runtimeMutationActiveTransaction, transaction))
                _runtimeMutationActiveTransaction = null;
        }
    }

    private void UntrackRuntimeMutationTransaction(
        RuntimeMutationDbContextTransaction transaction)
    {
        lock (_runtimeMutationActiveTransactionGate)
        {
            _runtimeMutationTrackedTransactions.Remove(transaction);
            if (ReferenceEquals(_runtimeMutationActiveTransaction, transaction))
                _runtimeMutationActiveTransaction = null;
        }
    }

    private static void RestoreRuntimeMutationTransactionFlow(
        RuntimeMutationTransactionFlow transactionFlow)
    {
        if (ReferenceEquals(RuntimeMutationTransaction.Value, transactionFlow))
            RuntimeMutationTransaction.Value = transactionFlow.Previous;
    }

    private void ReleaseRuntimeMutationTransactionLease(IDisposable? gateHandle)
    {
        var remainingDepth = Interlocked.Decrement(ref _runtimeMutationTransactionLeaseDepth);
        if (remainingDepth < 0)
        {
            Interlocked.Exchange(ref _runtimeMutationTransactionLeaseDepth, 0);
            throw new InvalidOperationException("Runtime mutation transaction lease depth became invalid.");
        }

        gateHandle?.Dispose();
    }

    private void ObserveRuntimeMutationEpochWhenTrackingStarts()
        => ChangeTracker.Tracked += (_, _) => EnsureRuntimeMutationEpochObserved();

    private long EnsureRuntimeMutationEpochObserved()
    {
        if (_observedRuntimeMutationEpoch.HasValue)
            return _observedRuntimeMutationEpoch.Value;

        var key = GetRuntimeMutationKey();
        var epoch = RuntimeMutationEpochs.GetOrAdd(key, 0);
        _observedRuntimeMutationEpoch = epoch;
        return epoch;
    }

    private string GetRuntimeMutationKey()
    {
        if (!string.IsNullOrWhiteSpace(_runtimeMutationKey))
            return _runtimeMutationKey;

        var connection = Database.GetDbConnection();
        var dataSource = connection.DataSource?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(dataSource) || string.Equals(dataSource, ":memory:", StringComparison.OrdinalIgnoreCase))
        {
            _runtimeMutationKey =
                $"memory:{connection.GetType().FullName}:{RuntimeHelpers.GetHashCode(connection)}";
            return _runtimeMutationKey;
        }

        try
        {
            _runtimeMutationKey = Path.GetFullPath(dataSource);
        }
        catch
        {
            _runtimeMutationKey = $"datasource:{dataSource}";
        }

        return _runtimeMutationKey;
    }

    private static RuntimeMutationOwnerScope EnterRuntimeMutationGateOwnerScope(
        IRuntimeMutationGateHandle gateHandle)
    {
        var previous = RuntimeMutationOwner.Value;
        var frame = new RuntimeMutationOwnerFrame(gateHandle.State, previous);
        RuntimeMutationOwner.Value = frame;
        return new RuntimeMutationOwnerScope(frame, previous);
    }

    private static bool TryAcquireRuntimeMutationOwnerBorrow(
        out RuntimeMutationGateBorrow ownerBorrow)
    {
        for (var frame = RuntimeMutationOwner.Value;
             frame is not null;
             frame = frame.Previous)
        {
            if (!frame.TryAcquireBorrow())
                continue;

            try
            {
                frame.State.EnterOwnerMutation();
                ownerBorrow = new RuntimeMutationGateBorrow(
                    frame,
                    ownsOwnerMutation: true);
                return true;
            }
            catch
            {
                frame.ReleaseBorrow();
                throw;
            }
        }

        ownerBorrow = null!;
        return false;
    }

    private static async Task<RuntimeMutationGateBorrow?> TryAcquireRuntimeMutationOwnerBorrowAsync(
        CancellationToken cancellationToken)
    {
        for (var frame = RuntimeMutationOwner.Value;
             frame is not null;
             frame = frame.Previous)
        {
            if (!frame.TryAcquireBorrow())
                continue;

            try
            {
                await frame.State.EnterOwnerMutationAsync(cancellationToken);
                return new RuntimeMutationGateBorrow(
                    frame,
                    ownsOwnerMutation: true);
            }
            catch
            {
                frame.ReleaseBorrow();
                throw;
            }
        }

        return null;
    }

    private static bool HasActiveRuntimeMutationOwner()
    {
        for (var frame = RuntimeMutationOwner.Value;
             frame is not null;
             frame = frame.Previous)
        {
            if (frame.IsActive)
                return true;
        }

        return false;
    }

    private static void ThrowIfRuntimeMutationOwnerIsSuppressed()
    {
        if (RuntimeMutationOwnerSuppressionContext.Value is not null)
        {
            throw new InvalidOperationException(
                "Runtime mutation owner authority cannot flow through an event or background callback.");
        }
    }

    private bool OwnsRuntimeMutationTransactionInCurrentFlow()
        => GetCurrentRuntimeMutationTransactionFlow() is { IsActive: true } transaction &&
           ReferenceEquals(transaction.Owner, this);

    private bool TryAcquireRuntimeMutationTransactionBorrow(
        out RuntimeMutationTransactionBorrow transactionBorrow)
    {
        var transaction = GetCurrentRuntimeMutationTransactionFlow();
        if (transaction is { IsActive: true } &&
            ReferenceEquals(transaction.Owner, this) &&
            transaction.TryAcquireBorrow())
        {
            transactionBorrow = new RuntimeMutationTransactionBorrow(transaction);
            return true;
        }

        transactionBorrow = null!;
        return false;
    }

    private void ThrowIfOwnedRuntimeMutationTransactionIsUnavailable()
    {
        var transaction = GetCurrentRuntimeMutationTransactionFlow();
        if (transaction is not null && ReferenceEquals(transaction.Owner, this))
        {
            throw new InvalidOperationException(
                transaction.IsActive
                    ? "The current logical flow's runtime mutation transaction is starting or closing and cannot accept another mutation."
                    : "A captured runtime mutation transaction flow is closed and cannot authorize a later mutation.");
        }
    }

    private bool OwnsRuntimeMutationExecutionInCurrentFlow()
    {
        for (var execution = RuntimeMutationExecution.Value;
             execution is not null;
             execution = execution.Previous)
        {
            if (!execution.IsActive)
                continue;
            return ReferenceEquals(execution.Owner, this);
        }

        return false;
    }

    private bool TryAcquireRuntimeMutationExecutionBorrow(
        out RuntimeMutationExecutionBorrow executionBorrow)
    {
        for (var execution = RuntimeMutationExecution.Value;
             execution is not null;
             execution = execution.Previous)
        {
            if (!ReferenceEquals(execution.Owner, this))
                continue;
            if (!execution.TryAcquireBorrow())
                continue;

            executionBorrow = new RuntimeMutationExecutionBorrow(execution);
            return true;
        }

        executionBorrow = null!;
        return false;
    }

    private void ThrowIfCrossContextRuntimeMutationIsActive()
    {
        var transaction = GetCurrentRuntimeMutationTransactionFlow();
        if (transaction is not null && !ReferenceEquals(transaction.Owner, this))
        {
            throw new InvalidOperationException(
                transaction.IsActive
                    ? "A different LocalDbContext cannot mutate while the current logical flow owns a runtime mutation transaction."
                    : "A captured closed runtime mutation transaction flow cannot authorize a mutation on another LocalDbContext.");
        }

        for (var execution = RuntimeMutationExecution.Value;
             execution is not null;
             execution = execution.Previous)
        {
            if (!execution.IsActive)
                continue;
            if (ReferenceEquals(execution.Owner, this))
                return;

            throw new InvalidOperationException(
                "A different LocalDbContext cannot start a nested runtime mutation operation in the same logical flow.");
        }
    }

    private static RuntimeMutationExecutionScope EnterRuntimeMutationExecutionScope(
        LocalDbContext owner,
        IRuntimeMutationGateHandle gateHandle)
    {
        var previous = RuntimeMutationExecution.Value;
        var frame = new RuntimeMutationExecutionFrame(
            owner,
            gateHandle.State,
            previous);
        RuntimeMutationExecution.Value = frame;
        return new RuntimeMutationExecutionScope(frame, previous);
    }

    private static RuntimeMutationTransactionFlow? GetCurrentRuntimeMutationTransactionFlow()
    {
        var transaction = RuntimeMutationTransaction.Value;
        while (transaction is { IsActive: false, WasActivated: false })
        {
            RuntimeMutationTransaction.Value = transaction.Previous;
            transaction = transaction.Previous;
        }
        return transaction;
    }

    private interface IRuntimeMutationGateHandle : IDisposable, IAsyncDisposable
    {
        RuntimeMutationGateState State { get; }
    }

    private sealed class RuntimeMutationGateState
    {
        private readonly SemaphoreSlim _ownerMutationGate = new(1, 1);
        private int _referenceCount = 1;
        private int _gateReleased;

        public void EnterOwnerMutation() => _ownerMutationGate.Wait();

        public Task EnterOwnerMutationAsync(CancellationToken cancellationToken)
            => _ownerMutationGate.WaitAsync(cancellationToken);

        public void ExitOwnerMutation() => _ownerMutationGate.Release();

        public bool TryRetain()
        {
            while (true)
            {
                var current = Volatile.Read(ref _referenceCount);
                if (current <= 0)
                    return false;
                if (Interlocked.CompareExchange(
                        ref _referenceCount,
                        checked(current + 1),
                        current) == current)
                {
                    return true;
                }
            }
        }

        public void Release()
        {
            var remaining = Interlocked.Decrement(ref _referenceCount);
            if (remaining < 0)
                throw new InvalidOperationException("Runtime mutation gate reference count became invalid.");
            if (remaining == 0 && Interlocked.Exchange(ref _gateReleased, 1) == 0)
                RuntimeMutationGate.Release();
        }
    }

    private sealed class RuntimeMutationGateLease : IRuntimeMutationGateHandle
    {
        private int _disposed;

        public RuntimeMutationGateState State { get; } = new();

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                State.Release();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RuntimeMutationGateBorrow(
        RuntimeMutationOwnerFrame ownerFrame,
        bool ownsOwnerMutation = false)
        : IRuntimeMutationGateHandle
    {
        private int _disposed;

        public RuntimeMutationGateState State => ownerFrame.State;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (ownsOwnerMutation)
                State.ExitOwnerMutation();
            ownerFrame.ReleaseBorrow();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RuntimeMutationExecutionBorrow(
        RuntimeMutationExecutionFrame executionFrame)
        : IRuntimeMutationGateHandle
    {
        private int _disposed;

        public RuntimeMutationGateState State => executionFrame.State;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                executionFrame.ReleaseBorrow();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RuntimeMutationTransactionBorrow(
        RuntimeMutationTransactionFlow transactionFlow)
        : IDisposable, IAsyncDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                transactionFlow.ReleaseBorrow();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class NoopRuntimeMutationGateLease : IDisposable, IAsyncDisposable
    {
        private NoopRuntimeMutationGateLease()
        {
        }

        public static NoopRuntimeMutationGateLease Instance { get; } = new();

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed record RuntimeMutationEntityStamp(
        string RuntimeMutationKey,
        long Epoch);

    private sealed record RuntimeMutationGuardFault(
        Exception PrimaryFailure,
        Exception CleanupFailure);

    private sealed class RuntimeMutationAsyncCompletionRequiredException(
        string message) : InvalidOperationException(message);


    private sealed class RuntimeMutationOwnerSuppression
    {
    }

    private sealed class RuntimeMutationOwnerFrame(
        RuntimeMutationGateState state,
        RuntimeMutationOwnerFrame? previous)
    {
        private readonly object _borrowGate = new();
        private TaskCompletionSource? _drained;
        private int _activeBorrowCount;
        private int _closed;

        public RuntimeMutationGateState State { get; } = state;
        public RuntimeMutationOwnerFrame? Previous { get; } = previous;
        public bool IsActive => Volatile.Read(ref _closed) == 0;

        public bool TryAcquireBorrow()
        {
            lock (_borrowGate)
            {
                if (_closed != 0 || !State.TryRetain())
                    return false;
                _activeBorrowCount++;
                return true;
            }
        }

        public void ReleaseBorrow()
        {
            TaskCompletionSource? drained = null;
            lock (_borrowGate)
            {
                _activeBorrowCount--;
                if (_activeBorrowCount < 0)
                {
                    _activeBorrowCount = 0;
                    throw new InvalidOperationException(
                        "Runtime mutation owner borrow count became invalid.");
                }

                if (_closed != 0 && _activeBorrowCount == 0)
                    drained = _drained;
            }

            State.Release();
            drained?.TrySetResult();
        }

        public Task CloseAndDrainAsync()
        {
            lock (_borrowGate)
            {
                _closed = 1;
                if (_activeBorrowCount == 0)
                    return Task.CompletedTask;
                _drained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }

    }

    private sealed class RuntimeMutationExecutionFrame(
        LocalDbContext owner,
        RuntimeMutationGateState state,
        RuntimeMutationExecutionFrame? previous)
    {
        private readonly object _borrowGate = new();
        private TaskCompletionSource? _drained;
        private int _activeBorrowCount;
        private int _closed;

        public LocalDbContext Owner { get; } = owner;
        public RuntimeMutationGateState State { get; } = state;
        public RuntimeMutationExecutionFrame? Previous { get; } = previous;
        public bool IsActive => Volatile.Read(ref _closed) == 0;

        public bool TryAcquireBorrow()
        {
            lock (_borrowGate)
            {
                if (_closed != 0 || !State.TryRetain())
                    return false;
                _activeBorrowCount++;
                return true;
            }
        }

        public void ReleaseBorrow()
        {
            TaskCompletionSource? drained = null;
            lock (_borrowGate)
            {
                _activeBorrowCount--;
                if (_activeBorrowCount < 0)
                {
                    _activeBorrowCount = 0;
                    throw new InvalidOperationException(
                        "Runtime mutation execution borrow count became invalid.");
                }

                if (_closed != 0 && _activeBorrowCount == 0)
                    drained = _drained;
            }

            State.Release();
            drained?.TrySetResult();
        }

        public Task CloseAndDrainAsync()
        {
            lock (_borrowGate)
            {
                _closed = 1;
                if (_activeBorrowCount == 0)
                    return Task.CompletedTask;
                _drained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }
    }

    private sealed class RuntimeMutationTransactionFlow(
        LocalDbContext owner,
        RuntimeMutationTransactionFlow? previous)
    {
        private readonly object _borrowGate = new();
        private RuntimeMutationGateState? _state;
        private TaskCompletionSource? _drained;
        private int _activeBorrowCount;
        private int _closed;

        public LocalDbContext Owner { get; } = owner;
        public RuntimeMutationTransactionFlow? Previous { get; } = previous;
        public bool IsActive => Volatile.Read(ref _closed) == 0;
        public bool WasActivated => Volatile.Read(ref _state) is not null;

        public void AttachState(RuntimeMutationGateState state)
        {
            lock (_borrowGate)
            {
                if (_closed != 0 || _state is not null)
                {
                    throw new InvalidOperationException(
                        "Runtime mutation transaction flow state cannot be attached more than once or after closing.");
                }

                _state = state;
            }
        }

        public bool TryAcquireBorrow()
        {
            lock (_borrowGate)
            {
                if (_closed != 0 || _state is null || !_state.TryRetain())
                    return false;
                _activeBorrowCount++;
                return true;
            }
        }

        public void ReleaseBorrow()
        {
            RuntimeMutationGateState state;
            TaskCompletionSource? drained = null;
            lock (_borrowGate)
            {
                state = _state ?? throw new InvalidOperationException(
                    "Runtime mutation transaction flow state is unavailable.");
                _activeBorrowCount--;
                if (_activeBorrowCount < 0)
                {
                    _activeBorrowCount = 0;
                    throw new InvalidOperationException(
                        "Runtime mutation transaction borrow count became invalid.");
                }

                if (_closed != 0 && _activeBorrowCount == 0)
                    drained = _drained;
            }

            state.Release();
            drained?.TrySetResult();
        }

        public Task CloseAndDrainAsync()
        {
            lock (_borrowGate)
            {
                _closed = 1;
                if (_activeBorrowCount == 0)
                    return Task.CompletedTask;
                _drained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return _drained.Task;
            }
        }

        public bool TryCloseWithoutBlocking()
        {
            lock (_borrowGate)
            {
                _closed = 1;
                if (_activeBorrowCount == 0)
                    return true;
                _drained ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                return false;
            }
        }
    }

    private sealed class RuntimeMutationDbContextTransaction(
        IDbContextTransaction inner,
        LocalDbContext owner,
        IDisposable? gateHandle,
        RuntimeMutationTransactionFlow transactionFlow)
        : IDbContextTransaction, IInfrastructure<DbTransaction>
    {
        private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
        private int _commitFailureCleanupCompleted;
        private int _providerDisposeStarted;
        private int _released;
        private int _transactionEnded;

        public Guid TransactionId => inner.TransactionId;
        public bool SupportsSavepoints => inner.SupportsSavepoints;
        DbTransaction IInfrastructure<DbTransaction>.Instance =>
            ((IInfrastructure<DbTransaction>)inner).Instance;

        public void Commit()
        {
            EnterSynchronousLifecycle();
            try
            {
                ThrowIfTransactionEnded();
                CloseTransactionFlowSynchronouslyOrThrow();
                DetachRootTransactionFlow();
                try
                {
                    inner.Commit();
                }
                catch (Exception primaryFailure)
                {
                    Volatile.Write(ref _transactionEnded, 1);
                    TryRollbackAfterCommitFailure(primaryFailure);
                    CompleteFailedTransaction(primaryFailure);
                    Volatile.Write(ref _commitFailureCleanupCompleted, 1);
                    throw;
                }

                Volatile.Write(ref _transactionEnded, 1);
                Release();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            if (!_lifecycleGate.Wait(0))
                return WaitForLifecycleRetryAsync(cancellationToken);
            DetachRootTransactionFlow();
            return CommitCoreAsync(cancellationToken);
        }

        private async Task CommitCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                ThrowIfTransactionEnded();
                await transactionFlow.CloseAndDrainAsync().ConfigureAwait(false);
                try
                {
                    await inner.CommitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception primaryFailure)
                {
                    Volatile.Write(ref _transactionEnded, 1);
                    await TryRollbackAfterCommitFailureAsync(primaryFailure).ConfigureAwait(false);
                    await CompleteFailedTransactionAsync(primaryFailure).ConfigureAwait(false);
                    Volatile.Write(ref _commitFailureCleanupCompleted, 1);
                    throw;
                }

                Volatile.Write(ref _transactionEnded, 1);
                Release();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public void Rollback()
        {
            if (IsCommitFailureCleanupCompleted())
                return;
            EnterSynchronousLifecycle();
            try
            {
                if (IsCommitFailureCleanupCompleted())
                    return;
                ThrowIfTransactionEnded();
                CloseTransactionFlowSynchronouslyOrThrow();
                DetachRootTransactionFlow();
                try
                {
                    inner.Rollback();
                }
                catch (Exception primaryFailure)
                {
                    Volatile.Write(ref _transactionEnded, 1);
                    CompleteFailedTransaction(primaryFailure);
                    throw;
                }

                Volatile.Write(ref _transactionEnded, 1);
                Release();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public Task RollbackAsync(CancellationToken cancellationToken = default)
        {
            if (IsCommitFailureCleanupCompleted())
                return Task.CompletedTask;
            if (cancellationToken.IsCancellationRequested)
                return Task.FromCanceled(cancellationToken);
            if (!_lifecycleGate.Wait(0))
                return WaitForLifecycleRetryAsync(
                    cancellationToken,
                    allowCommitFailureCleanupNoop: true);
            DetachRootTransactionFlow();
            return RollbackCoreAsync(cancellationToken);
        }

        private async Task RollbackCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (IsCommitFailureCleanupCompleted())
                    return;
                ThrowIfTransactionEnded();
                await transactionFlow.CloseAndDrainAsync().ConfigureAwait(false);
                try
                {
                    await inner.RollbackAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception primaryFailure)
                {
                    Volatile.Write(ref _transactionEnded, 1);
                    await CompleteFailedTransactionAsync(primaryFailure).ConfigureAwait(false);
                    throw;
                }

                Volatile.Write(ref _transactionEnded, 1);
                Release();
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public void CreateSavepoint(string name)
        {
            ThrowIfTransactionEnded();
            inner.CreateSavepoint(name);
        }

        public Task CreateSavepointAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTransactionEnded();
            return inner.CreateSavepointAsync(name, cancellationToken);
        }

        public void RollbackToSavepoint(string name)
        {
            ThrowIfTransactionEnded();
            inner.RollbackToSavepoint(name);
        }

        public Task RollbackToSavepointAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTransactionEnded();
            return inner.RollbackToSavepointAsync(name, cancellationToken);
        }

        public void ReleaseSavepoint(string name)
        {
            ThrowIfTransactionEnded();
            inner.ReleaseSavepoint(name);
        }

        public Task ReleaseSavepointAsync(
            string name,
            CancellationToken cancellationToken = default)
        {
            ThrowIfTransactionEnded();
            return inner.ReleaseSavepointAsync(name, cancellationToken);
        }

        public void Dispose()
        {
            EnterSynchronousLifecycle();
            try
            {
                if (Volatile.Read(ref _providerDisposeStarted) != 0)
                    return;

                var releaseRequired = Volatile.Read(ref _transactionEnded) == 0;
                if (releaseRequired)
                    CloseTransactionFlowSynchronouslyOrThrow();
                DetachRootTransactionFlow();
                if (Interlocked.Exchange(ref _providerDisposeStarted, 1) != 0)
                    return;
                try
                {
                    inner.Dispose();
                }
                catch (Exception primaryFailure)
                {
                    if (releaseRequired)
                        Volatile.Write(ref _transactionEnded, 1);
                    CompleteFailedDispose(primaryFailure);
                    throw;
                }

                owner.UntrackRuntimeMutationTransaction(this);

                if (releaseRequired)
                {
                    Volatile.Write(ref _transactionEnded, 1);
                    Release();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public ValueTask DisposeAsync()
        {
            DetachRootTransactionFlow();
            return new ValueTask(DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Interlocked.Exchange(ref _providerDisposeStarted, 1) != 0)
                    return;

                var releaseRequired = Volatile.Read(ref _transactionEnded) == 0;
                if (releaseRequired)
                    await transactionFlow.CloseAndDrainAsync().ConfigureAwait(false);
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception primaryFailure)
                {
                    if (releaseRequired)
                        Volatile.Write(ref _transactionEnded, 1);
                    await CompleteFailedDisposeAsync(primaryFailure).ConfigureAwait(false);
                    throw;
                }

                owner.UntrackRuntimeMutationTransaction(this);

                if (releaseRequired)
                {
                    Volatile.Write(ref _transactionEnded, 1);
                    Release();
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private void CompleteFailedTransaction(Exception primaryFailure)
        {
            Exception? cleanupFailure = null;
            var providerTransactionTerminated = false;
            if (Interlocked.Exchange(ref _providerDisposeStarted, 1) == 0)
            {
                try
                {
                    inner.Dispose();
                    providerTransactionTerminated = true;
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (!providerTransactionTerminated)
            {
                providerTransactionTerminated =
                    owner.TryQuarantineRuntimeMutationConnection(out var quarantineFailure);
                if (quarantineFailure is not null)
                {
                    cleanupFailure = CombineCleanupFailures(
                        cleanupFailure,
                        quarantineFailure);
                }
            }

            CompleteFailedCleanup(
                primaryFailure,
                cleanupFailure,
                providerTransactionTerminated);
        }

        private void TryRollbackAfterCommitFailure(Exception primaryFailure)
        {
            try
            {
                inner.Rollback();
            }
            catch (Exception rollbackFailure)
            {
                AttachCleanupFailure(primaryFailure, rollbackFailure);
            }
        }

        private async Task TryRollbackAfterCommitFailureAsync(Exception primaryFailure)
        {
            try
            {
                await inner.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure)
            {
                AttachCleanupFailure(primaryFailure, rollbackFailure);
            }
        }

        private async Task CompleteFailedTransactionAsync(Exception primaryFailure)
        {
            Exception? cleanupFailure = null;
            var providerTransactionTerminated = false;
            if (Interlocked.Exchange(ref _providerDisposeStarted, 1) == 0)
            {
                try
                {
                    await inner.DisposeAsync().ConfigureAwait(false);
                    providerTransactionTerminated = true;
                }
                catch (Exception exception)
                {
                    cleanupFailure = exception;
                }
            }

            if (!providerTransactionTerminated)
            {
                var quarantine = await owner.TryQuarantineRuntimeMutationConnectionAsync()
                    .ConfigureAwait(false);
                providerTransactionTerminated = quarantine.Terminated;
                if (quarantine.Failure is not null)
                {
                    cleanupFailure = CombineCleanupFailures(
                        cleanupFailure,
                        quarantine.Failure);
                }
            }

            CompleteFailedCleanup(
                primaryFailure,
                cleanupFailure,
                providerTransactionTerminated);
        }

        private void CompleteFailedDispose(Exception primaryFailure)
        {
            var providerTransactionTerminated =
                owner.TryQuarantineRuntimeMutationConnection(out var quarantineFailure);
            CompleteFailedCleanup(
                primaryFailure,
                quarantineFailure,
                providerTransactionTerminated);
        }

        private async Task CompleteFailedDisposeAsync(Exception primaryFailure)
        {
            var quarantine = await owner.TryQuarantineRuntimeMutationConnectionAsync()
                .ConfigureAwait(false);
            CompleteFailedCleanup(
                primaryFailure,
                quarantine.Failure,
                quarantine.Terminated);
        }

        private void CompleteFailedCleanup(
            Exception primaryFailure,
            Exception? cleanupFailure,
            bool providerTransactionTerminated)
        {
            if (!providerTransactionTerminated)
            {
                var guardFailure = cleanupFailure ?? new InvalidOperationException(
                    "Runtime mutation provider transaction termination could not be confirmed.");
                owner.FaultRuntimeMutationGuard(primaryFailure, guardFailure);
            }

            AttachCleanupFailure(primaryFailure, cleanupFailure);
            owner.UntrackRuntimeMutationTransaction(this);
            try
            {
                Release();
            }
            catch (Exception releaseFailure)
            {
                AttachCleanupFailure(
                    primaryFailure,
                    CombineCleanupFailures(cleanupFailure, releaseFailure));
            }
        }

        private void Release()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.UnregisterActiveRuntimeMutationTransaction(this);
                RestoreRuntimeMutationTransactionFlow(transactionFlow);
                owner.ReleaseRuntimeMutationTransactionLease(gateHandle);
            }
        }

        private void ThrowIfTransactionEnded()
        {
            if (Volatile.Read(ref _transactionEnded) != 0 ||
                Volatile.Read(ref _providerDisposeStarted) != 0)
            {
                throw new InvalidOperationException(
                    "The runtime mutation transaction has already completed or been disposed.");
            }
        }

        private async Task WaitForLifecycleRetryAsync(
            CancellationToken cancellationToken,
            bool allowCommitFailureCleanupNoop = false)
        {
            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (allowCommitFailureCleanupNoop && IsCommitFailureCleanupCompleted())
                    return;
                ThrowIfTransactionEnded();
            }
            finally
            {
                _lifecycleGate.Release();
            }
            throw new InvalidOperationException(
                "Another runtime mutation transaction lifecycle operation completed first. Retry against the current transaction state.");
        }

        private bool IsCommitFailureCleanupCompleted()
            => Volatile.Read(ref _commitFailureCleanupCompleted) != 0;

        private void EnterSynchronousLifecycle()
        {
            if (!_lifecycleGate.Wait(0))
            {
                throw new RuntimeMutationAsyncCompletionRequiredException(
                    "Another runtime mutation transaction lifecycle operation is still running. Retry asynchronously after it completes.");
            }
        }

        private void CloseTransactionFlowSynchronouslyOrThrow()
        {
            if (!transactionFlow.TryCloseWithoutBlocking())
            {
                throw new RuntimeMutationAsyncCompletionRequiredException(
                    "The runtime mutation transaction still has an active asynchronous child operation. Use CommitAsync, RollbackAsync, or DisposeAsync to drain it safely.");
            }
        }

        internal void DetachRootTransactionFlow()
            => RestoreRuntimeMutationTransactionFlow(transactionFlow);
    }

    private sealed class RuntimeMutationOwnerScope(
        RuntimeMutationOwnerFrame frame,
        RuntimeMutationOwnerFrame? previous) : IDisposable, IAsyncDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            var drain = Close();
            drain?.GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            var drain = Close();
            if (drain is not null)
                await drain.ConfigureAwait(false);
        }

        private Task? Close()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return null;
            var drain = frame.CloseAndDrainAsync();
            if (ReferenceEquals(RuntimeMutationOwner.Value, frame))
                RuntimeMutationOwner.Value = previous;
            return drain;
        }
    }

    private sealed class RuntimeMutationExecutionScope(
        RuntimeMutationExecutionFrame frame,
        RuntimeMutationExecutionFrame? previous) : IDisposable, IAsyncDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            var drain = Close();
            drain?.GetAwaiter().GetResult();
        }

        public async ValueTask DisposeAsync()
        {
            var drain = Close();
            if (drain is not null)
                await drain.ConfigureAwait(false);
        }

        private Task? Close()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return null;
            var drain = frame.CloseAndDrainAsync();
            if (ReferenceEquals(RuntimeMutationExecution.Value, frame))
                RuntimeMutationExecution.Value = previous;
            return drain;
        }
    }

    private sealed class RuntimeMutationOwnerSuppressionScope(
        RuntimeMutationOwnerSuppression suppression,
        RuntimeMutationOwnerSuppression? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (ReferenceEquals(RuntimeMutationOwnerSuppressionContext.Value, suppression))
                RuntimeMutationOwnerSuppressionContext.Value = previous;
        }
    }

    private void PreserveDirtyMarkersForModifiedSyncGraphs()
    {
        ChangeTracker.DetectChanges();

        foreach (var entry in ChangeTracker.Entries()
                     .Where(entry =>
                         entry.State == EntityState.Modified &&
                         entry.Entity is ILocalSyncEntity { IsDirty: true }))
        {
            var dirtyProperty =
                entry.Property(nameof(ILocalSyncEntity.IsDirty));
            var updatedAtProperty =
                entry.Property(nameof(ILocalSyncEntity.UpdatedAtUtc));
            var hasModifiedPayloadProperty = entry.Properties.Any(property =>
                property.IsModified &&
                property.Metadata.Name is not
                    nameof(ILocalSyncEntity.IsDirty) and not
                    nameof(ILocalSyncEntity.Revision) and not
                    nameof(ILocalSyncEntity.UpdatedAtUtc));
            var becameDirty =
                dirtyProperty.IsModified &&
                dirtyProperty.OriginalValue is false &&
                dirtyProperty.CurrentValue is true;
            entry.Property(nameof(ILocalSyncEntity.IsDirty)).IsModified = true;
            if ((hasModifiedPayloadProperty || becameDirty) &&
                (!updatedAtProperty.IsModified ||
                 Equals(
                     updatedAtProperty.CurrentValue,
                     updatedAtProperty.OriginalValue)))
            {
                AdvanceDirtyMutationTimestamp(entry);
            }
        }

        var modifiedInvoiceIds = ChangeTracker.Entries<LocalInvoiceLine>()
            .Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.InvoiceId)
            .ToHashSet();
        foreach (var invoiceEntry in ChangeTracker.Entries<LocalInvoice>()
                     .Where(entry => modifiedInvoiceIds.Contains(entry.Entity.Id)))
        {
            MarkTrackedSyncRootDirty(invoiceEntry);
        }

        var modifiedTransferIds = ChangeTracker.Entries<LocalInventoryTransferLine>()
            .Where(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted)
            .Select(entry => entry.Entity.TransferId)
            .ToHashSet();
        foreach (var transferEntry in ChangeTracker.Entries<LocalInventoryTransfer>()
                     .Where(entry => modifiedTransferIds.Contains(entry.Entity.Id)))
        {
            MarkTrackedSyncRootDirty(transferEntry);
        }
    }

    private static void MarkTrackedSyncRootDirty<T>(Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T> entry)
        where T : class, ILocalSyncEntity
    {
        if (entry.Entity.IsDirty &&
            entry.State is (EntityState.Unchanged or EntityState.Modified))
        {
            entry.Property(entity => entity.IsDirty).IsModified = true;
            var updatedAtProperty =
                entry.Property(entity => entity.UpdatedAtUtc);
            if (!updatedAtProperty.IsModified ||
                updatedAtProperty.CurrentValue ==
                updatedAtProperty.OriginalValue)
            {
                AdvanceDirtyMutationTimestamp(entry);
            }
        }
    }

    private static void AdvanceDirtyMutationTimestamp(
        Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry)
    {
        var property =
            entry.Property(nameof(ILocalSyncEntity.UpdatedAtUtc));
        var current = NormalizeUtc((DateTime)property.CurrentValue!);
        var original = NormalizeUtc((DateTime)property.OriginalValue!);
        var baseline = current >= original ? current : original;
        var now = DateTime.UtcNow;
        property.CurrentValue =
            now > baseline
                ? now
                : baseline == DateTime.MaxValue
                    ? baseline
                    : baseline.AddTicks(1);
        property.IsModified = true;
    }

    private static DateTime NormalizeUtc(DateTime value)
        => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };

    public DbSet<LocalCompanyProfile> CompanyProfiles => Set<LocalCompanyProfile>();
    public DbSet<LocalUnit> Units => Set<LocalUnit>();
    public DbSet<LocalCustomerCategory> CustomerCategories => Set<LocalCustomerCategory>();
    public DbSet<LocalPriceGradeOption> PriceGradeOptions => Set<LocalPriceGradeOption>();
    public DbSet<LocalTradeTypeOption> TradeTypeOptions => Set<LocalTradeTypeOption>();
    public DbSet<LocalItemCategoryOption> ItemCategoryOptions => Set<LocalItemCategoryOption>();
    public DbSet<LocalCustomerMaster> CustomerMasters => Set<LocalCustomerMaster>();
    public DbSet<LocalCustomer> Customers => Set<LocalCustomer>();
    public DbSet<LocalCustomerContract> CustomerContracts => Set<LocalCustomerContract>();
    public DbSet<LocalItem> Items => Set<LocalItem>();
    public DbSet<LocalItemPriceGrade> ItemPriceGrades => Set<LocalItemPriceGrade>();
    public DbSet<LocalInvoice> Invoices => Set<LocalInvoice>();
    public DbSet<LocalInvoiceLine> InvoiceLines => Set<LocalInvoiceLine>();
    public DbSet<LocalPayment> Payments => Set<LocalPayment>();
    public DbSet<LocalSetting> Settings => Set<LocalSetting>();
    public DbSet<LocalRecentSelection> RecentSelections => Set<LocalRecentSelection>();
    public DbSet<LocalAttachmentSelection> AttachmentSelections => Set<LocalAttachmentSelection>();
    public DbSet<LocalSyncDiagnosticEvent> SyncDiagnosticEvents => Set<LocalSyncDiagnosticEvent>();
    public DbSet<LocalSyncOutboxEntry> SyncOutboxEntries => Set<LocalSyncOutboxEntry>();
    public DbSet<LocalDeferredRecycleBinPurgeRecord> DeferredRecycleBinPurgeRecords => Set<LocalDeferredRecycleBinPurgeRecord>();
    public DbSet<LocalInventoryTransferTombstoneConflict> InventoryTransferTombstoneConflicts => Set<LocalInventoryTransferTombstoneConflict>();
    public DbSet<LocalTransaction> Transactions => Set<LocalTransaction>();
    public DbSet<LocalTransactionAttachment> TransactionAttachments => Set<LocalTransactionAttachment>();
    public DbSet<LocalOffice> Offices => Set<LocalOffice>();
    public DbSet<LocalWarehouse> Warehouses => Set<LocalWarehouse>();
    public DbSet<LocalInvoiceLineSerial> InvoiceLineSerials => Set<LocalInvoiceLineSerial>();
    public DbSet<LocalInventoryMovement> InventoryMovements => Set<LocalInventoryMovement>();
    public DbSet<LocalStockLayer> StockLayers => Set<LocalStockLayer>();
    public DbSet<LocalCostAllocation> CostAllocations => Set<LocalCostAllocation>();
    public DbSet<LocalItemWarehouseStock> ItemWarehouseStocks => Set<LocalItemWarehouseStock>();
    public DbSet<LocalSerialLedger> SerialLedgers => Set<LocalSerialLedger>();
    public DbSet<LocalAuditLog> AuditLogs => Set<LocalAuditLog>();
    public DbSet<LocalInventoryTransfer> InventoryTransfers => Set<LocalInventoryTransfer>();
    public DbSet<LocalInventoryTransferLine> InventoryTransferLines => Set<LocalInventoryTransferLine>();
    public DbSet<LocalRentalManagementCompany> RentalManagementCompanies => Set<LocalRentalManagementCompany>();
    public DbSet<LocalRentalBillingProfile> RentalBillingProfiles => Set<LocalRentalBillingProfile>();
    public DbSet<LocalRentalAsset> RentalAssets => Set<LocalRentalAsset>();
    public DbSet<LocalRentalAssetAssignmentHistory> RentalAssetAssignmentHistories => Set<LocalRentalAssetAssignmentHistory>();
    public DbSet<LocalRentalBillingLog> RentalBillingLogs => Set<LocalRentalBillingLog>();

    protected override void OnConfiguring(DbContextOptionsBuilder options)
    {
        options.AddInterceptors(RuntimeMutationCommandInterceptor.Instance);

        if (!options.IsConfigured)
            options.UseSqlite($"Data Source={AppPaths.LocalDbFile}");
    }

    protected override void OnModelCreating(ModelBuilder model)
    {
        // Soft-delete query filters
        model.Entity<LocalCompanyProfile>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalUnit>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomerCategory>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPriceGradeOption>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTradeTypeOption>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalItemCategoryOption>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomerMaster>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomer>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalCustomerContract>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalItem>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalItemPriceGrade>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInvoice>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalPayment>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalOffice>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalWarehouse>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInventoryTransfer>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTransactionAttachment>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalManagementCompany>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalBillingProfile>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalAsset>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalAssetAssignmentHistory>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalRentalBillingLog>().HasQueryFilter(e => !e.IsDeleted);

        // InvoiceLine: no ILocalSyncEntity, filter inline
        model.Entity<LocalInvoiceLine>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalInventoryTransferLine>().HasQueryFilter(e => !e.IsDeleted);

        // Settings: key is PK
        model.Entity<LocalSetting>().HasKey(s => s.Key);
        model.Entity<LocalSyncDiagnosticEvent>().HasKey(e => e.Id);

        // Indexes for sync pull efficiency
        model.Entity<LocalCompanyProfile>().HasIndex(e => e.Revision);
        model.Entity<LocalUnit>().HasIndex(e => e.Revision);
        model.Entity<LocalPriceGradeOption>().HasIndex(e => e.Revision);
        model.Entity<LocalTradeTypeOption>().HasIndex(e => e.Revision);
        model.Entity<LocalItemCategoryOption>().HasIndex(e => e.Revision);
        model.Entity<LocalCustomer>().HasIndex(e => e.Revision);
        model.Entity<LocalCustomerContract>().HasIndex(e => e.Revision);
        model.Entity<LocalItem>().HasIndex(e => e.Revision);
        model.Entity<LocalItemPriceGrade>().HasIndex(e => e.Revision);
        model.Entity<LocalInvoice>().HasIndex(e => e.Revision);
        model.Entity<LocalPayment>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalManagementCompany>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalBillingProfile>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalAsset>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalAssetAssignmentHistory>().HasIndex(e => e.Revision);
        model.Entity<LocalRentalBillingLog>().HasIndex(e => e.Revision);

        // InvoiceLine owned by Invoice
        model.Entity<LocalInvoice>()
            .HasMany(i => i.Lines)
            .WithOne(l => l.Invoice)
            .HasForeignKey(l => l.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<LocalInvoice>()
            .HasMany(i => i.Payments)
            .WithOne(p => p.Invoice)
            .HasForeignKey(p => p.InvoiceId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<LocalTransaction>()
            .HasMany(transaction => transaction.Attachments)
            .WithOne(attachment => attachment.Transaction)
            .HasForeignKey(attachment => attachment.TransactionId)
            .OnDelete(DeleteBehavior.Cascade);

        model.Entity<LocalInventoryTransfer>()
            .HasMany(t => t.Lines)
            .WithOne(l => l.Transfer)
            .HasForeignKey(l => l.TransferId)
            .OnDelete(DeleteBehavior.Cascade);

        // RecentSelections index
        model.Entity<LocalRecentSelection>()
            .HasIndex(r => new { r.EntityType, r.EntityId })
            .IsUnique();

        model.Entity<LocalAttachmentSelection>()
            .HasKey(s => new { s.CustomerKey, s.DocCode });
        model.Entity<LocalAttachmentSelection>()
            .HasIndex(s => s.CustomerKey);
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => e.LastOccurredAtUtc);
        model.Entity<LocalSyncOutboxEntry>().HasKey(entry => entry.Id);
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => entry.MutationId)
            .IsUnique();
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => new { entry.Status, entry.PreparedAtUtc });
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => new { entry.TenantCode, entry.OfficeCode, entry.ResponsibleOfficeCode, entry.Status, entry.PreparedAtUtc });
        model.Entity<LocalSyncOutboxEntry>()
            .HasIndex(entry => new
            {
                entry.EntityName,
                entry.EntityId,
                entry.TenantCode,
                entry.OfficeCode,
                entry.ResponsibleOfficeCode,
                entry.BusinessDatabaseName,
                entry.DeviceId,
                entry.SessionId,
                entry.UserId,
                entry.Status,
                entry.PreparedAtUtc
            })
            .HasDatabaseName("IX_SyncOutboxEntries_SupersedeScope_Status_PreparedAtUtc");
        model.Entity<LocalDeferredRecycleBinPurgeRecord>()
            .HasKey(record => record.Id);
        model.Entity<LocalDeferredRecycleBinPurgeRecord>()
            .Property(record => record.Id)
            .ValueGeneratedNever();
        model.Entity<LocalDeferredRecycleBinPurgeRecord>()
            .HasIndex(record => new
            {
                record.BusinessDatabaseName,
                record.TenantCode,
                record.OfficeCode,
                record.ResponsibleOfficeCode,
                record.Kind,
                record.EntityId
            })
            .HasDatabaseName("IX_DeferredRecycleBinPurgeRecords_Scope_Entity");
        model.Entity<LocalDeferredRecycleBinPurgeRecord>()
            .HasIndex(record => new { record.AppliedAtUtc, record.NextAttemptAtUtc })
            .HasDatabaseName("IX_DeferredRecycleBinPurgeRecords_AppliedAtUtc_NextAttemptAtUtc");
        model.Entity<LocalInventoryTransferTombstoneConflict>()
            .HasKey(conflict => new
            {
                conflict.BusinessDatabaseName,
                conflict.TransferId
            });
        model.Entity<LocalInventoryTransferTombstoneConflict>()
            .Property(conflict => conflict.TransferId)
            .ValueGeneratedNever();
        model.Entity<LocalInventoryTransferTombstoneConflict>()
            .HasIndex(conflict => new { conflict.Status, conflict.UpdatedAtUtc })
            .HasDatabaseName("IX_InventoryTransferTombstoneConflicts_Status_UpdatedAtUtc");
        model.Entity<LocalInventoryTransferTombstoneConflict>()
            .HasIndex(conflict => new
            {
                conflict.BusinessDatabaseName,
                conflict.TenantCode,
                conflict.SourceOfficeCode,
                conflict.TargetOfficeCode,
                conflict.Status
            })
            .HasDatabaseName("IX_InventoryTransferTombstoneConflicts_BusinessScope_Status");
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => new { e.Status, e.LastOccurredAtUtc });
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => new { e.Category, e.Subcategory });
        model.Entity<LocalSyncDiagnosticEvent>()
            .HasIndex(e => new { e.SyncPhase, e.Status });
        model.Entity<LocalCustomerContract>()
            .HasIndex(contract => contract.CustomerId);
        model.Entity<LocalCustomerContract>()
            .HasIndex(contract => new { contract.CustomerId, contract.IsPrimary });

        model.Entity<LocalPriceGradeOption>()
            .HasIndex(option => option.Name);
        model.Entity<LocalTradeTypeOption>()
            .HasIndex(option => option.Name);
        model.Entity<LocalItemCategoryOption>()
            .HasIndex(option => option.Name);
        model.Entity<LocalCompanyProfile>()
            .HasIndex(profile => new { profile.OfficeCode, profile.ProfileName });
        model.Entity<LocalCompanyProfile>()
            .HasIndex(profile => new { profile.OfficeCode, profile.IsDefaultForOffice });
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.OfficeCode);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.ResponsibleOfficeCode);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => new { customer.OfficeCode, customer.IsDeleted })
            .HasDatabaseName("IX_Customers_IntegrityOfficeActive");
        model.Entity<LocalCustomer>()
            .HasIndex(customer => new { customer.ResponsibleOfficeCode, customer.IsDeleted })
            .HasDatabaseName("IX_Customers_IntegrityResponsibleActive");
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.NameOriginal);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => customer.NameMatchKey);
        model.Entity<LocalCustomer>()
            .HasIndex(customer => new { customer.IsDeleted, customer.BusinessNumber })
            .HasDatabaseName("IX_Customers_Search_BusinessNumber");
        model.Entity<LocalCustomer>()
            .HasIndex(customer => new { customer.IsDeleted, customer.NameOriginal })
            .HasDatabaseName("IX_Customers_Search_NameOriginal");
        model.Entity<LocalCustomer>()
            .HasIndex(customer => new { customer.IsDeleted, customer.NameMatchKey })
            .HasDatabaseName("IX_Customers_Search_NameMatchKey");
        model.Entity<LocalItem>()
            .HasIndex(item => new { item.TenantCode, item.OfficeCode });
        model.Entity<LocalItem>()
            .HasIndex(item => new { item.OfficeCode, item.IsDeleted })
            .HasDatabaseName("IX_Items_IntegrityOfficeActive");
        model.Entity<LocalItemPriceGrade>()
            .HasIndex(grade => grade.ItemId);
        model.Entity<LocalItemPriceGrade>()
            .HasIndex(grade => grade.PriceGradeOptionId);
        model.Entity<LocalItemPriceGrade>()
            .HasIndex(grade => new { grade.ItemId, grade.PriceGradeOptionId })
            .HasDatabaseName("IX_ItemPriceGrades_ItemOption")
            .IsUnique();

        model.Entity<LocalOffice>()
            .HasIndex(o => o.Code)
            .IsUnique();
        model.Entity<LocalWarehouse>()
            .HasIndex(w => w.Code)
            .IsUnique();
        model.Entity<LocalWarehouse>()
            .HasIndex(w => w.OfficeCode);
        model.Entity<LocalWarehouse>()
            .HasIndex(w => new { w.OfficeCode, w.IsDeleted, w.IsActive })
            .HasDatabaseName("IX_Warehouses_IntegrityOfficeActive");

        model.Entity<LocalInvoice>()
            .HasIndex(i => i.VersionGroupId);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.IsLatestVersion);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.LinkedRentalBillingProfileId);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.LinkedRentalBillingRunId);
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.IsDeleted, i.LinkedRentalBillingRunId })
            .HasDatabaseName("IX_Invoices_RentalRunReference");
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.IsDeleted, i.LinkedRentalBillingProfileId, i.LinkedRentalBillingRunId })
            .HasDatabaseName("IX_Invoices_RentalProfileReference");
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.SourceWarehouseCode);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.PurchaseReceivingStatus);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.TaxInvoiceNumber);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.ResponsibleOfficeCode);
        model.Entity<LocalInvoice>()
            .HasIndex(i => i.OfficeCode);
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.ResponsibleOfficeCode, i.IsDeleted, i.IsLatestVersion })
            .HasDatabaseName("IX_Invoices_IntegrityResponsibleLatest");
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.TenantCode, i.ResponsibleOfficeCode, i.IsLatestVersion, i.InvoiceDate });
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.CustomerId, i.IsLatestVersion, i.InvoiceDate });
        model.Entity<LocalInvoice>()
            .HasIndex(i => new { i.VoucherType, i.IsLatestVersion, i.InvoiceDate });
        model.Entity<LocalInvoiceLine>()
            .HasIndex(line => new { line.InvoiceId, line.IsDeleted })
            .HasDatabaseName("IX_InvoiceLines_InvoiceActiveAggregate");
        model.Entity<LocalPayment>()
            .HasIndex(payment => new { payment.InvoiceId, payment.IsDeleted })
            .HasDatabaseName("IX_Payments_InvoiceActiveAggregate");
        model.Entity<LocalInvoice>()
            .Property(i => i.VatMode)
            .HasMaxLength(20)
            .HasDefaultValue(InvoiceVatModes.Included);

        model.Entity<LocalInvoiceLineSerial>()
            .HasIndex(s => new { s.InvoiceId, s.InvoiceLineId });
        model.Entity<LocalInvoiceLineSerial>()
            .HasIndex(s => s.SerialNumber);

        model.Entity<LocalInventoryMovement>()
            .HasIndex(m => new { m.ItemId, m.WarehouseCode, m.OccurredDate });
        model.Entity<LocalInventoryMovement>()
            .HasIndex(m => new { m.ItemId, m.IsActive, m.WarehouseCode })
            .HasDatabaseName("IX_InventoryMovements_ItemActiveWarehouse");
        model.Entity<LocalInventoryMovement>()
            .HasIndex(m => m.InvoiceId);

        model.Entity<LocalStockLayer>()
            .HasIndex(l => new { l.ItemId, l.WarehouseCode, l.ReceiptDate });
        model.Entity<LocalCostAllocation>()
            .HasIndex(a => new { a.SalesInvoiceId, a.SalesInvoiceLineId });
        model.Entity<LocalItemWarehouseStock>()
            .HasKey(s => new { s.ItemId, s.WarehouseCode });
        model.Entity<LocalSerialLedger>()
            .HasIndex(s => s.SerialNumber)
            .IsUnique();

        model.Entity<LocalRentalManagementCompany>()
            .HasIndex(company => company.Code)
            .IsUnique();
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => profile.ProfileKey)
            .IsUnique();
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => profile.OfficeCode);
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => profile.ResponsibleOfficeCode);
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.OfficeCode, profile.IsDeleted, profile.IsActive })
            .HasDatabaseName("IX_RentalBillingProfiles_IntegrityOfficeActive");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.ResponsibleOfficeCode, profile.IsDeleted, profile.IsActive })
            .HasDatabaseName("IX_RentalBillingProfiles_IntegrityResponsibleActive");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.ManagementCompanyCode, profile.IsDeleted, profile.IsActive })
            .HasDatabaseName("IX_RentalBillingProfiles_IntegrityManagementActive");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.ResponsibleOfficeCode, profile.IsDeleted, profile.IsActive });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.ManagementCompanyCode, profile.IsDeleted, profile.IsActive });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.CustomerId, profile.IsDeleted });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.BillingStatus, profile.IsDeleted });
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.IsDeleted, profile.CustomerName, profile.ItemName })
            .HasDatabaseName("IX_RentalBillingProfiles_ListSort");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.IsDeleted, profile.CustomerName, profile.ItemName })
            .HasDatabaseName("IX_RentalBillingProfiles_TenantListSort");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.ResponsibleOfficeCode, profile.IsDeleted, profile.CustomerName, profile.ItemName })
            .HasDatabaseName("IX_RentalBillingProfiles_TenantOfficeListSort");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.TenantCode, profile.ManagementCompanyCode, profile.IsDeleted, profile.CustomerName, profile.ItemName })
            .HasDatabaseName("IX_RentalBillingProfiles_TenantManagementListSort");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.IsDeleted, profile.CustomerName })
            .HasDatabaseName("IX_RentalBillingProfiles_Search_CustomerName");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.IsDeleted, profile.BusinessNumber })
            .HasDatabaseName("IX_RentalBillingProfiles_Search_BusinessNumber");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.IsDeleted, profile.ItemName })
            .HasDatabaseName("IX_RentalBillingProfiles_Search_ItemName");
        model.Entity<LocalRentalBillingProfile>()
            .HasIndex(profile => new { profile.IsDeleted, profile.Notes })
            .HasDatabaseName("IX_RentalBillingProfiles_Search_Notes");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.AssetKey })
            .HasDatabaseName("IX_RentalAssets_AssetKey")
            .HasFilter("COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(TRIM(\"AssetKey\"), '') <> ''")
            .IsUnique();
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementId })
            .HasDatabaseName("IX_RentalAssets_ManagementId")
            .HasFilter("COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(TRIM(\"ManagementId\"), '') <> ''")
            .IsUnique();
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_ManagementNumber")
            .HasFilter("COALESCE(\"IsDeleted\", 0) = 0 AND COALESCE(TRIM(\"ManagementNumber\"), '') <> ''")
            .IsUnique();
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => asset.OfficeCode);
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => asset.ResponsibleOfficeCode);
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.OfficeCode, asset.IsDeleted })
            .HasDatabaseName("IX_RentalAssets_IntegrityOfficeActive");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.ResponsibleOfficeCode, asset.IsDeleted })
            .HasDatabaseName("IX_RentalAssets_IntegrityResponsibleActive");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.ManagementCompanyCode, asset.IsDeleted })
            .HasDatabaseName("IX_RentalAssets_IntegrityManagementActive");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ResponsibleOfficeCode, asset.IsDeleted, asset.AssetStatus });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementCompanyCode, asset.IsDeleted, asset.AssetStatus });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ResponsibleOfficeCode, asset.IsDeleted, asset.CustomerName, asset.ManagementNumber });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementCompanyCode, asset.IsDeleted, asset.CustomerName, asset.ManagementNumber });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.CustomerName, asset.ManagementNumber });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.IsDeleted, asset.CustomerName, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_TenantListSort");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.BillingProfileId, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.CustomerId, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.ItemCategoryName, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.BillingEligibilityStatus, asset.IsDeleted });
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.ItemCategoryName, asset.CustomerName, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_Filter_ItemCategoryListSort");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.AssetStatus, asset.CustomerName, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_Filter_StatusListSort");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.BillingProfileId, asset.CustomerId, asset.AssetStatus })
            .HasDatabaseName("IX_RentalAssets_ReplacementCandidatePrefilter");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.BillingProfileId, asset.BillingEligibilityStatus, asset.AssetStatus, asset.CustomerName, asset.CurrentCustomerName, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_UnlinkedBillingCandidates");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ResponsibleOfficeCode, asset.IsDeleted, asset.BillingProfileId, asset.CustomerName, asset.CurrentCustomerName, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_TenantOfficeBillingProfileSort");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.TenantCode, asset.ManagementCompanyCode, asset.IsDeleted, asset.BillingProfileId, asset.CustomerName, asset.CurrentCustomerName, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_TenantManagementBillingProfileSort");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.ManagementNumber })
            .HasDatabaseName("IX_RentalAssets_Search_ManagementNumber");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.CustomerName })
            .HasDatabaseName("IX_RentalAssets_Search_CustomerName");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.CurrentCustomerName })
            .HasDatabaseName("IX_RentalAssets_Search_CurrentCustomerName");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.ItemCategoryName })
            .HasDatabaseName("IX_RentalAssets_Search_ItemCategoryName");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.ItemName })
            .HasDatabaseName("IX_RentalAssets_Search_ItemName");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.MachineNumber })
            .HasDatabaseName("IX_RentalAssets_Search_MachineNumber");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.InstallLocation })
            .HasDatabaseName("IX_RentalAssets_Search_InstallLocation");
        model.Entity<LocalRentalAsset>()
            .HasIndex(asset => new { asset.IsDeleted, asset.InstallSiteName })
            .HasDatabaseName("IX_RentalAssets_Search_InstallSiteName");
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => new { history.AssetId, history.IsCurrent });
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => new { history.ResponsibleOfficeCode, history.IsDeleted })
            .HasDatabaseName("IX_RentalAssetAssignmentHistories_IntegrityResponsibleActive");
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => new { history.AssetId, history.IsCurrent, history.LinkedAtUtc })
            .HasDatabaseName("IX_RentalAssetAssignmentHistories_AssetTimeline");
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => history.BillingProfileId);
        model.Entity<LocalRentalAssetAssignmentHistory>()
            .HasIndex(history => history.LinkedAtUtc);
        model.Entity<LocalRentalBillingLog>()
            .HasIndex(log => new { log.BillingProfileId, log.BillingYearMonth })
            .IsUnique();
        model.Entity<LocalRentalBillingLog>()
            .HasIndex(log => log.OfficeCode);
        model.Entity<LocalRentalBillingLog>()
            .HasIndex(log => log.ResponsibleOfficeCode);
        model.Entity<LocalAuditLog>()
            .HasIndex(a => new { a.EntityName, a.EntityId, a.CreatedAtUtc });
        model.Entity<LocalInventoryTransfer>()
            .HasIndex(t => t.TransferDate);
        model.Entity<LocalInventoryTransfer>()
            .HasIndex(t => t.TransferNumber);
        model.Entity<LocalInventoryTransfer>()
            .HasIndex(t => new { t.FromWarehouseCode, t.ToWarehouseCode });
        model.Entity<LocalInventoryTransferLine>()
            .HasIndex(l => new { l.TransferId, l.ItemId });
        model.Entity<LocalTransactionAttachment>()
            .HasIndex(attachment => attachment.TransactionId);
        model.Entity<LocalTransactionAttachment>()
            .HasIndex(attachment => new { attachment.TransactionId, attachment.VerificationStatus });

        // Transactions
        model.Entity<LocalTransaction>().HasQueryFilter(e => !e.IsDeleted);
        model.Entity<LocalTransaction>().HasIndex(e => e.CustomerId);
        model.Entity<LocalTransaction>().HasIndex(e => e.TransactionDate);
        model.Entity<LocalTransaction>().HasIndex(e => e.OfficeCode);
        model.Entity<LocalTransaction>().HasIndex(e => e.ResponsibleOfficeCode);
        model.Entity<LocalTransaction>().HasIndex(e => e.LinkedRentalBillingProfileId);
        model.Entity<LocalTransaction>().HasIndex(e => e.LinkedRentalBillingRunId);
        model.Entity<LocalTransaction>()
            .HasIndex(e => new { e.IsDeleted, e.LinkedRentalBillingRunId })
            .HasDatabaseName("IX_Transactions_RentalRunReference");
        model.Entity<LocalTransaction>()
            .HasIndex(e => new { e.IsDeleted, e.LinkedRentalBillingProfileId, e.LinkedRentalBillingRunId })
            .HasDatabaseName("IX_Transactions_RentalProfileReference");
        model.Entity<LocalTransaction>()
            .HasIndex(e => new { e.IsDeleted, e.LinkedInvoiceId })
            .HasDatabaseName("IX_Transactions_LinkedInvoiceReference");
        model.Entity<LocalTransactionAttachment>().HasIndex(e => e.Revision);
    }
}
