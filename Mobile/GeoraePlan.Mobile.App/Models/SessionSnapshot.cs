using System;

namespace GeoraePlan.Mobile.App.Models;

public sealed class SessionSnapshot
{
    public const string SettingsEditPermission = "Settings.Edit";
    public const string DataBackupRestorePermission = "Data.BackupRestore";
    public const string CustomerEditPermission = "Customer.Edit";
    public const string ItemEditPermission = "Item.Edit";
    public const string InvoiceEditPermission = "Invoice.Edit";
    public const string PaymentEditPermission = "Payment.Edit";

    public static SessionSnapshot Empty { get; } = new();

    public bool IsAuthenticated { get; init; }
    public string Username { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string TenantCode { get; init; } = string.Empty;
    public string OfficeCode { get; init; } = string.Empty;
    public string ScopeType { get; init; } = string.Empty;
    public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();
    public DateTime? ExpiresAtUtc { get; init; }
    public bool IsAdmin => string.Equals(Role, "Admin", StringComparison.OrdinalIgnoreCase);
    public bool CanViewIntegrityReport => IsAdmin || HasPermission(SettingsEditPermission);
    public bool CanManageRecycleBin => IsAdmin || HasPermission(DataBackupRestorePermission);
    public bool CanEditCustomers => IsAdmin || HasPermission(CustomerEditPermission);
    public bool CanEditItems => IsAdmin || HasPermission(ItemEditPermission);
    public bool CanCreateInvoices => IsAdmin || HasPermission(InvoiceEditPermission);
    public bool CanCreatePayments => IsAdmin || HasPermission(PaymentEditPermission);

    public bool HasPermission(string permission)
        => Permissions.Any(current => string.Equals(current, permission, StringComparison.OrdinalIgnoreCase));
}
