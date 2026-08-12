namespace 거래플랜.Desktop.App.Infrastructure;

internal sealed record CustomerInlineSaveFailure(
    Guid CustomerId,
    int Generation,
    string Label);

/// <summary>
/// Tracks the latest inline-save generation and any unresolved save failure
/// independently for each customer.
/// </summary>
internal sealed class CustomerInlineSaveStateTracker
{
    private sealed class CustomerState
    {
        public int LatestGeneration { get; set; }

        public string Label { get; set; } = string.Empty;

        public CustomerInlineSaveFailure? UnresolvedFailure { get; set; }
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, CustomerState> _states = [];

    public int Begin(Guid customerId, string label)
    {
        ArgumentNullException.ThrowIfNull(label);

        lock (_gate)
        {
            if (!_states.TryGetValue(customerId, out var state))
            {
                state = new CustomerState();
                _states.Add(customerId, state);
            }

            state.LatestGeneration = checked(state.LatestGeneration + 1);
            state.Label = label;
            return state.LatestGeneration;
        }
    }

    public bool IsLatest(Guid customerId, int generation)
    {
        lock (_gate)
        {
            return _states.TryGetValue(customerId, out var state) &&
                   state.LatestGeneration == generation;
        }
    }

    public bool MarkFailure(Guid customerId, int generation)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(customerId, out var state) ||
                state.LatestGeneration != generation)
            {
                return false;
            }

            state.UnresolvedFailure = new CustomerInlineSaveFailure(
                customerId,
                generation,
                state.Label);
            return true;
        }
    }

    public bool MarkSuccess(Guid customerId, int generation)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(customerId, out var state) ||
                state.LatestGeneration != generation)
            {
                return false;
            }

            // A successful latest attempt supersedes an unresolved failure from
            // an older attempt for this customer, but never another customer.
            state.UnresolvedFailure = null;
            return true;
        }
    }

    public IReadOnlyList<CustomerInlineSaveFailure> SnapshotUnresolvedFailures()
    {
        lock (_gate)
        {
            return _states.Values
                .Select(state => state.UnresolvedFailure)
                .Where(failure => failure is not null)
                .Select(failure => failure!)
                .OrderBy(failure => failure.Label, StringComparer.Ordinal)
                .ThenBy(failure => failure.CustomerId)
                .ToArray();
        }
    }
}
