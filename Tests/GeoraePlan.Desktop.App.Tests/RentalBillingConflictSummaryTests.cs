using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalBillingConflictSummaryTests
{
    [Theory]
    [InlineData("청구중", false)]
    [InlineData("미수", false)]
    [InlineData("완료", false)]
    [InlineData("청구설정 필요", true)]
    public void GroupedRow_PreservesConflictPriorityAndKnownAmounts(string otherStatus, bool unlinked)
    {
        var conflict = new RentalBillingViewRow {
            SelectionId = Guid.NewGuid(), Source = new LocalRentalBillingProfile { Id = Guid.NewGuid() },
            DisplayStatus = "확인 필요", CurrentBillingRunStatus = "확인 필요", HasDataIssue = true,
            DataIssueSummary = "청구기간 충돌", CompletionStatus = "미완료"
        };
        var other = new RentalBillingViewRow {
            SelectionId = Guid.NewGuid(), Source = new LocalRentalBillingProfile { Id = Guid.NewGuid() },
            HasPersistedProfile = !unlinked, GroupedUnlinkedAssetCount = unlinked ? 1 : 0,
            DisplayStatus = otherStatus, CurrentBillingRunStatus = otherStatus,
            CurrentBilledAmount = 100000m, SettledAmount = 50000m, OutstandingAmount = 50000m
        };
        var service = new RentalStateService(null!);
        var method = typeof(RentalStateService).GetMethod("CreateGroupedBillingViewRow", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (var rows in new[] { new[] { conflict, other }, new[] { other, conflict } })
        {
            var grouped = Assert.IsType<RentalBillingViewRow>(method.Invoke(service, new object[] { rows, "same-customer" }));
            Assert.Equal("확인 필요", grouped.CurrentBillingRunStatus);
            Assert.Equal("확인 필요", grouped.DisplayStatus);
            Assert.Equal("확인 필요", grouped.SettlementStatusDisplay);
            Assert.True(grouped.HasCurrentBillingConflict);
            Assert.Contains("청구기간 충돌", grouped.DataIssueSummary);
            Assert.Equal(100000m, grouped.CurrentBilledAmount);
            Assert.Equal(50000m, grouped.SettledAmount);
            Assert.Equal(50000m, grouped.OutstandingAmount);
            Assert.Equal("확인 필요", grouped.CurrentBilledAmountDisplay);
        }
        Assert.Equal("확인 필요", conflict.CurrentBillingRunStatus);
        Assert.Equal(otherStatus, other.CurrentBillingRunStatus);
    }

    [Theory]
    [InlineData("확인 필요", true)]
    [InlineData("Planned", false)]
    [InlineData("Completed", false)]
    public void AmountDisplays_SeparateUnconfirmedAmountsFromNumericValues(string status, bool unknown)
    {
        var row = new RentalBillingViewRow { CurrentBillingRunStatus = status, CurrentBilledAmount = 1000m,
            SettledAmount = 500m, OutstandingAmount = 500m, SettlementStatus = "부분입금" };
        Assert.Equal(unknown, row.HasCurrentBillingConflict);
        Assert.Equal(unknown ? "확인 필요" : 1000m.ToString("N0"), row.CurrentBilledAmountDisplay);
        Assert.Equal(unknown ? "확인 필요" : 500m.ToString("N0"), row.SettledAmountDisplay);
        Assert.Equal(unknown ? "확인 필요" : 500m.ToString("N0"), row.OutstandingAmountDisplay);
        Assert.Equal(unknown ? "확인 필요" : "부분입금", row.SettlementStatusDisplay);
        Assert.Equal(1000m, row.CurrentBilledAmount);
        Assert.Equal(500m, row.SettledAmount);
        Assert.Equal(500m, row.OutstandingAmount);
    }

    [Fact]
    public void SummaryNotice_UpdatesWhenConflictingRowsAppearAndDisappear()
    {
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        var notifications = new List<string?>();
        vm.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        vm.TotalOutstandingAmount = 50000m;
        vm.HasCurrentBillingConflict = true;
        Assert.Equal("확인된 미수금", vm.OutstandingAmountSummaryLabel);
        Assert.Contains("기간 확인", vm.OutstandingAmountSummaryNotice);
        Assert.Contains(nameof(vm.OutstandingAmountSummaryLabel), notifications);
        Assert.Contains(nameof(vm.OutstandingAmountSummaryNotice), notifications);
        notifications.Clear();
        vm.HasCurrentBillingConflict = false;
        Assert.Equal("총 미수금", vm.OutstandingAmountSummaryLabel);
        Assert.Empty(vm.OutstandingAmountSummaryNotice);
        Assert.Equal(50000m, vm.TotalOutstandingAmount);
        Assert.Contains(nameof(vm.OutstandingAmountSummaryLabel), notifications);
        Assert.Contains(nameof(vm.OutstandingAmountSummaryNotice), notifications);
    }
}
