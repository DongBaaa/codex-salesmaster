using System.IO;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

internal static class TestSeedSyncConflictDiagnostics
{
    private static readonly HashSet<string> AllowedEntityNames = new(
        StringComparer.Ordinal)
    {
        "CustomerMaster",
        "Customer",
        "CustomerContract",
        "Item",
        "Invoice",
        "Payment",
        "Transaction",
        "TransactionRecord",
        "TransactionAttachment",
        "InventoryTransfer",
        "RentalManagementCompany",
        "RentalBillingProfile",
        "RentalAsset",
        "RentalAssetAssignmentHistory",
        "RentalBillingLog",
        "ItemWarehouseStock"
    };

    internal static IReadOnlyList<string> BuildLines(
        IEnumerable<ConflictLogDto> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);

        return conflicts
            .GroupBy(conflict => new
            {
                Entity = SafeEntityName(conflict.EntityName),
                ReasonKind = ClassifyReason(conflict.Reason)
            })
            .OrderBy(group => group.Key.Entity, StringComparer.Ordinal)
            .ThenBy(group => group.Key.ReasonKind, StringComparer.Ordinal)
            .Select(group =>
                $"seed_sync_server_conflict_group entity={group.Key.Entity} " +
                $"reason_kind={group.Key.ReasonKind} count={group.Count()}")
            .ToList();
    }

    internal static IReadOnlyList<string> BuildAcceptedRevisionLines(
        IEnumerable<SyncAcceptedRevisionDto> acceptedRevisions)
    {
        ArgumentNullException.ThrowIfNull(acceptedRevisions);

        return acceptedRevisions
            .Where(revision => revision.EntityId != Guid.Empty)
            .GroupBy(revision => SafeEntityName(revision.EntityName))
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
                "seed_sync_server_accepted_revision_group " +
                $"entity={group.Key} count={group.Count()}")
            .ToList();
    }

    internal static void WriteIfEnabled(
        IEnumerable<ConflictLogDto> conflicts,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!IsTruthy(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_TEST_SEED_MODE")))
        {
            return;
        }

        foreach (var line in BuildLines(conflicts))
            output.WriteLine(line);
    }

    internal static void WriteAcceptedRevisionsIfEnabled(
        IEnumerable<SyncAcceptedRevisionDto> acceptedRevisions,
        TextWriter output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!IsTruthy(
                Environment.GetEnvironmentVariable(
                    "GEORAEPLAN_TEST_SEED_MODE")))
        {
            return;
        }

        foreach (var line in BuildAcceptedRevisionLines(acceptedRevisions))
            output.WriteLine(line);
    }

    private static string SafeEntityName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return AllowedEntityNames.Contains(normalized) ? normalized : "unknown";
    }

    private static string ClassifyReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "none";

        var normalized = value.Trim();
        if (ContainsAny(
                normalized,
                "Mutation id was already processed with a different entity, expected revision, or payload"))
            return "mutation_replay_mismatch";
        if (ContainsAny(
                normalized,
                "Mutation id is duplicated",
                "mutation id is reused by conflicting rows"))
            return "mutation_duplicate";
        if (ContainsAny(normalized, "revision mismatch"))
            return "revision_conflict";
        if (ContainsAny(normalized, "Server version is newer"))
            return "server_newer";
        if (ContainsAny(
                normalized,
                "same invoice id",
                "protected invoice"))
            return "protected_invoice_structure";
        if (string.Equals(
                normalized,
                RentalBillingTemplateAssetCoverageRules
                    .ExplicitCoverageConflictMessage,
                StringComparison.Ordinal))
        {
            return "rental_template_coverage";
        }
        if (ContainsAny(
                normalized,
                "Referenced invoice line item was not found"))
            return "invoice_item_reference_missing";
        if (ContainsAny(
                normalized,
                "Referenced item was not found"))
            return "item_reference_missing";
        if (ContainsAny(
                normalized,
                "Referenced rental billing profile was not found",
                "Referenced rental billing profile is missing or deleted"))
            return "rental_billing_profile_reference_missing";
        if (ContainsAny(
                normalized,
                "Referenced invoice was not found",
                "Referenced invoice customer was not found"))
            return "invoice_reference_missing";
        if (ContainsAny(
                normalized,
                "Referenced customer was not found",
                "Referenced customer is missing or deleted"))
            return "customer_reference_missing";
        if (ContainsAny(
                normalized,
                "Referenced rental asset was not found",
                "was not found",
                "missing or deleted"))
            return "reference_missing";
        if (ContainsAny(
                normalized,
                "outside the writable office scope",
                "outside the readable office scope",
                "outside the writable warehouse scope",
                "outside the writable payment office scope",
                "Invoice source warehouse is outside",
                "Current account cannot modify this office scope"))
            return "scope_rejected";
        if (ContainsAny(
                normalized,
                "tenant and office scope values are inconsistent"))
            return "scope_inconsistent";
        if (ContainsAny(
                normalized,
                "invoice version metadata",
                "invoice version chain",
                "invoice version",
                "version group",
                "Previous invoice version"))
            return "invoice_version_metadata";
        if (ContainsAny(
                normalized,
                "requires exactly one Transaction and one Payment",
                "Payment cannot claim an existing Transaction",
                "paired Transaction",
                "paired Payment",
                "paired Payment and Transaction",
                "controlled by a Payment with the same id",
                "Linked transaction does not point to a payment invoice",
                "Linked transaction invoice does not match the payment invoice"))
            return "payment_transaction_pair";
        if (ContainsAny(
                normalized,
                "Payment amount must be greater than zero",
                "Payment amount exceeds current outstanding balance",
                "Transaction amount exceeds current outstanding balance"))
            return "payment_amount";
        if (ContainsAny(
                normalized,
                "quantity must be greater than zero",
                "numeric(18,2)"))
            return "numeric_contract";
        if (ContainsAny(
                normalized,
                "unproven generation"))
            return "unproven_dependency_generation";
        if (ContainsAny(
                normalized,
                "requires Payment.Edit permission"))
            return "permission_rejected";

        return "other";
    }

    private static bool ContainsAny(string value, params string[] candidates)
        => candidates.Any(candidate =>
            value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal) ||
           string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "yes", StringComparison.OrdinalIgnoreCase);
}
