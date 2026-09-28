using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MobilePaymentSaveAcknowledgementTests
{
    [Theory]
    [InlineData("Accepted", "Accepted", 0, "", false)]
    [InlineData("Unknown", "Unknown", 1, "offline", true)]
    [InlineData("Unknown", "Unknown", 0, "", true)]
    [InlineData("Accepted", "Accepted", 1, "", true)]
    [InlineData("Accepted", "Unknown", 0, "", true)]
    [InlineData("Accepted", "Rejected", 0, "conflict", true)]
    [InlineData("Accepted", "Accepted", 0, "refresh failed", true)]
    [InlineData("Rejected", "Unknown", 1, "forbidden", false)]
    public void PendingOrPartiallyCompletedSaveMustBeAcknowledgedBeforeLeaving(
        string paymentOutcome,
        string transactionOutcome,
        int attachments,
        string error,
        bool expected)
    {
        var payment = Enum.Parse<MobileImmediateMutationOutcome>(paymentOutcome);
        var transaction = Enum.Parse<MobileImmediateMutationOutcome>(transactionOutcome);
        var state = new MobileSyncState { LastError = error };
        for (var i = 0; i < attachments; i++)
            state.PendingPaymentAttachments.Add(new PendingPaymentAttachmentRecord());
        var result = new MobileImmediatePaymentSaveResult(state, payment, transaction);

        Assert.Equal(expected, result.RequiresSaveAcknowledgement);
        if (payment == MobileImmediateMutationOutcome.Rejected)
            Assert.False(result.CanInvokeSuccessCallback);
    }
}
