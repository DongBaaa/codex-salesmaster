using 거래플랜.Server.Api.Domain;

namespace 거래플랜.Server.Api.Services;

internal static class InvoiceAuthorPolicy
{
    // Call only after an accepted content write, never on receipt replay,
    // delete, version-flag maintenance, settlement recalculation or startup.
    public static void RecordAcceptedSave(
        Invoice invoice, ICurrentUserContext currentUser, bool isNew, Invoice? previous = null)
    {
        var username = currentUser.Username?.Trim() ?? string.Empty;
        if (isNew)
            invoice.CreatedByUsername = previous is null ? username : previous.CreatedByUsername;
        invoice.LastSavedByUsername = username;
        invoice.LastSavedAtUtc = DateTime.UtcNow;
    }
}
