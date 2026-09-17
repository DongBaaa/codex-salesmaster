using System.Reflection;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class NoFixedDayEditorTests
{
    [Fact]
    public async Task BillingEditor_ShowsManualPolicyWithoutNumericDayOrCalculatedDeadline()
    {
        var vm = new RentalBillingViewModel(null!, null!, new SessionState());
        try
        {
            vm.EditContractDate = new DateTime(2026, 9, 1);
            vm.EditBillingCycleMonths = 1;
            vm.EditBillingAdvanceMode = "당월";
            Assert.Contains("당월", vm.BillingAdvanceModeOptions);
            vm.EditBillingDay = 31;
            vm.EditBillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay;
            Assert.Contains(RentalBillingScheduleRules.BillingDayModeNoFixedDay, vm.BillingDayModeOptions);
            Assert.False(vm.IsFixedBillingDayMode);
            Assert.Contains("지정일 없음", vm.BillingSchedulePreviewText);
            Assert.Contains("예상 결제일: 지정 없음", vm.BillingSchedulePreviewText);
            Assert.Contains("서류 발송일: 지정 없음", vm.DocumentIssuePreviewText);
            Assert.DoesNotContain("2026-09-25", vm.BillingSchedulePreviewText);
        }
        finally
        {
            await vm.CancelAndDrainPendingBackgroundWorkAsync();
        }
    }

    [Fact]
    public void OnboardingEditor_PreservesManualPolicyAndMonthPreview()
    {
        var vm = new RentalCustomerOnboardingViewModel(null!, null!, new SessionState());
        typeof(RentalCustomerOnboardingViewModel).GetMethod("BeginAutoSaveSuppression", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        vm.BillingStartDate = new DateTime(2026, 9, 1);
        vm.BillingCycleMonths = 1;
        vm.BillingAdvanceMode = "당월";
        Assert.Contains("당월", vm.BillingAdvanceModeOptions);
        vm.BillingDay = 31;
        vm.BillingDayMode = RentalBillingScheduleRules.BillingDayModeNoFixedDay;
        Assert.Contains(RentalBillingScheduleRules.BillingDayModeNoFixedDay, vm.BillingDayModeOptions);
        Assert.Equal(0, vm.BillingDay);
        Assert.False(vm.IsFixedBillingDayMode);
        Assert.Equal("2026-09", vm.BillingPreviewPeriod);
        Assert.Contains("예상 결제일: 지정 없음", vm.BillingSchedulePreviewText);
        Assert.Contains("서류 발송일: 지정 없음", vm.DocumentIssuePreviewText);
    }
}
