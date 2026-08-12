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

    [Fact]
    public void MobileCustomerAndItemWrites_PreserveStampedMutationAcrossDirectSaveFallbackQueue()
    {
        var repositoryRoot = FindRepositoryRoot();
        var mobileRoot = Path.Combine(
            repositoryRoot.FullName,
            "Mobile",
            "GeoraePlan.Mobile.App");
        var customerEdit = File.ReadAllText(Path.Combine(
            mobileRoot,
            "Pages",
            "CustomerEditPage.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var itemEdit = File.ReadAllText(Path.Combine(
            mobileRoot,
            "Pages",
            "ItemEditPage.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        var syncCoordinator = File.ReadAllText(Path.Combine(
            mobileRoot,
            "Services",
            "SyncCoordinator.cs"))
            .Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Contains(
            "dto.MutationId = BuildMutationId(\"customer\", dto.Id);",
            customerEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "dto.MutationCreatedAtUtc = now;",
            customerEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "dto.ExpectedRevision = _source?.Revision ?? 0;",
            customerEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueCustomerDraftAsync(\n                dto,\n                apiOwner,\n                reason)",
            customerEdit,
            StringComparison.Ordinal);

        Assert.Contains(
            "dto.MutationId = BuildMutationId(\"item\", dto.Id);",
            itemEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "dto.MutationCreatedAtUtc = now;",
            itemEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "dto.ExpectedRevision = _source?.Revision ?? 0;",
            itemEdit,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueItemDraftAsync(\n                dto,\n                apiOwner,\n                reason)",
            itemEdit,
            StringComparison.Ordinal);

        Assert.Contains(
            "QueueCustomerDraftAsync(\n        CustomerDto customer,\n        MobileSessionOwner owner",
            syncCoordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "QueueItemDraftAsync(\n        ItemDto item,\n        MobileSessionOwner owner",
            syncCoordinator,
            StringComparison.Ordinal);
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
