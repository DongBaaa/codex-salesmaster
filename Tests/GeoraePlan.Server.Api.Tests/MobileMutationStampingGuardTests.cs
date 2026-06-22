using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class MobileMutationStampingGuardTests
{
    [Fact]
    public void MobileInvoiceAndPaymentWrites_StampMutationIdsForRetryDeduplication()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mobileRoot = Path.Combine(repositoryRoot.FullName, "Mobile", "GeoraePlan.Mobile.App", "ViewModels");

        var invoiceDraft = File.ReadAllText(Path.Combine(mobileRoot, "InvoiceDraftViewModel.cs"));
        var paymentDraft = File.ReadAllText(Path.Combine(mobileRoot, "PaymentDraftViewModel.cs"));

        Assert.Contains("MutationId = forSave ? BuildMutationId(\"invoice\", invoiceId)", invoiceDraft, StringComparison.Ordinal);
        Assert.Contains("MutationCreatedAtUtc = forSave ? now", invoiceDraft, StringComparison.Ordinal);
        Assert.Contains("ExpectedRevision = _editingInvoice?.Revision ?? 0", invoiceDraft, StringComparison.Ordinal);

        Assert.Contains("MutationId = BuildMutationId(\"payment\", paymentId)", paymentDraft, StringComparison.Ordinal);
        Assert.Contains("MutationId = BuildMutationId(\"transaction\", paymentId)", paymentDraft, StringComparison.Ordinal);
        Assert.Contains("MutationCreatedAtUtc = now", paymentDraft, StringComparison.Ordinal);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("*.sln").Any())
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing a solution file was not found.");
    }
}
