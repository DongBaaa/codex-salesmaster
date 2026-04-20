namespace 거래플랜.Desktop.App.Services;

internal static class IntegrityIssueReviewPolicy
{
    private static readonly HashSet<string> LocalRoutineRepairCandidateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "out_of_scope_customers",
        "out_of_scope_items",
        "out_of_scope_invoices",
        "out_of_scope_transactions",
        "out_of_scope_rental_profiles",
        "out_of_scope_rental_assets",
        "inventory_current_stock_snapshot_mismatch",
        "inventory_nonstock_snapshot_residue",
        "cross_tenant_inventory_transfers",
        "orphan_item_warehouse_stock_refs",
        "orphan_stock_layer_item_refs",
        "orphan_inventory_movement_item_refs",
        "orphan_serial_ledger_item_refs"
    };

    private static readonly HashSet<string> ServerRoutineRepairCandidateCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "item_stock_snapshot_mismatch",
        "cross_tenant_inventory_transfers"
    };

    public static bool IsRoutineRepairCandidateForLocal(string? code)
        => !string.IsNullOrWhiteSpace(code) && LocalRoutineRepairCandidateCodes.Contains(code.Trim());

    public static bool IsRoutineRepairCandidateForServer(string? code)
        => !string.IsNullOrWhiteSpace(code) && ServerRoutineRepairCandidateCodes.Contains(code.Trim());
}
