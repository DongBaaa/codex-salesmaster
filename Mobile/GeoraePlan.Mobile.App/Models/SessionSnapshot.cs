using System;

namespace GeoraePlan.Mobile.App.Models;

public sealed class SessionSnapshot
{
    public const string SettingsEditPermission = "Settings.Edit";
    public const string CompanyProfileEditPermission = "CompanyProfile.Edit";
    public const string DataBackupRestorePermission = "Data.BackupRestore";
    public const string CustomerEditPermission = "Customer.Edit";
    public const string ItemEditPermission = "Item.Edit";
    public const string InvoiceEditPermission = "Invoice.Edit";
    public const string PaymentEditPermission = "Payment.Edit";
    public const string DeliveryEditPermission = "Delivery.Edit";
    public const string RentalProfileEditPermission = "Rental.ProfileEdit";
    public const string RentalAssetEditPermission = "Rental.AssetEdit";
    public const string RentalSettingsEditPermission = "Rental.SettingsEdit";
    public const string RentalEditAllPermission = "Rental.EditAll";

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
    public bool CanEditSettings => IsAdmin || HasPermission(SettingsEditPermission);
    public bool CanEditCompanyProfiles => IsAdmin || HasPermission(CompanyProfileEditPermission);
    public bool CanViewIntegrityReport => IsAdmin || HasPermission(SettingsEditPermission);
    public bool CanManageRecycleBin => IsAdmin || HasPermission(DataBackupRestorePermission);
    public bool CanEditCustomers => IsAdmin || HasPermission(CustomerEditPermission);
    public bool CanEditItems => IsAdmin || HasPermission(ItemEditPermission);
    public bool CanCreateInvoices => IsAdmin || HasPermission(InvoiceEditPermission);
    public bool CanCreatePayments => IsAdmin || HasPermission(PaymentEditPermission);
    public bool CanEditDelivery => IsAdmin || HasPermission(DeliveryEditPermission);
    public bool CanEditRentalSettings => IsAdmin || HasPermission(RentalSettingsEditPermission);
    public bool CanEditRentalProfiles => IsAdmin || HasPermission(RentalProfileEditPermission) || HasPermission(RentalEditAllPermission);
    public bool CanEditRentalAssets => IsAdmin || HasPermission(RentalAssetEditPermission) || HasPermission(RentalEditAllPermission);

    public bool HasPermission(string permission)
        => Permissions.Any(current => string.Equals(current, permission, StringComparison.OrdinalIgnoreCase));
}
