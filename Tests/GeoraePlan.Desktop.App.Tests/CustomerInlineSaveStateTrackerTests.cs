using 거래플랜.Desktop.App.Infrastructure;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class CustomerInlineSaveStateTrackerTests
{
    [Fact]
    public void CrossCustomerSuccess_DoesNotClearAnotherFailure_AndLaterLatestSuccessClearsIt()
    {
        var tracker = new CustomerInlineSaveStateTracker();
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();

        var customerAFailedGeneration = tracker.Begin(customerA, "Customer A");
        Assert.True(tracker.MarkFailure(customerA, customerAFailedGeneration));

        var customerBGeneration = tracker.Begin(customerB, "Customer B");
        Assert.True(tracker.MarkSuccess(customerB, customerBGeneration));

        var unresolved = Assert.Single(tracker.SnapshotUnresolvedFailures());
        Assert.Equal(customerA, unresolved.CustomerId);
        Assert.Equal(customerAFailedGeneration, unresolved.Generation);
        Assert.Equal("Customer A", unresolved.Label);

        var customerARetryGeneration = tracker.Begin(customerA, "Customer A");
        Assert.True(tracker.MarkSuccess(customerA, customerARetryGeneration));
        Assert.Empty(tracker.SnapshotUnresolvedFailures());
    }

    [Fact]
    public void StaleCompletion_CannotCreateOrClearTheLatestFailure()
    {
        var tracker = new CustomerInlineSaveStateTracker();
        var customerId = Guid.NewGuid();
        var staleGeneration = tracker.Begin(customerId, "Customer");
        var latestGeneration = tracker.Begin(customerId, "Customer");

        Assert.False(tracker.IsLatest(customerId, staleGeneration));
        Assert.True(tracker.IsLatest(customerId, latestGeneration));
        Assert.False(tracker.MarkFailure(customerId, staleGeneration));
        Assert.True(tracker.MarkFailure(customerId, latestGeneration));
        Assert.False(tracker.MarkSuccess(customerId, staleGeneration));

        var unresolved = Assert.Single(tracker.SnapshotUnresolvedFailures());
        Assert.Equal(latestGeneration, unresolved.Generation);

        Assert.True(tracker.MarkSuccess(customerId, latestGeneration));
        Assert.Empty(tracker.SnapshotUnresolvedFailures());
    }

    [Fact]
    public void ConcurrentCustomers_AreTrackedIndependently()
    {
        var tracker = new CustomerInlineSaveStateTracker();
        var customerIds = Enumerable.Range(0, 64)
            .Select(_ => Guid.NewGuid())
            .ToArray();

        Parallel.ForEach(
            customerIds,
            customerId =>
            {
                var generation = tracker.Begin(customerId, customerId.ToString("N"));
                Assert.True(tracker.MarkFailure(customerId, generation));
            });

        var unresolved = tracker.SnapshotUnresolvedFailures();
        Assert.Equal(customerIds.Length, unresolved.Count);
        Assert.Equal(
            customerIds.OrderBy(id => id),
            unresolved.Select(failure => failure.CustomerId).OrderBy(id => id));
    }
}
