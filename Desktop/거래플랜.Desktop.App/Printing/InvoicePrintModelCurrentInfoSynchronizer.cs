namespace 거래플랜.Desktop.App.Printing;

/// <summary>
/// Keeps saved print customization data while refreshing business party fields
/// that must remain linked to the current customer and company master records.
/// </summary>
public static class InvoicePrintModelCurrentInfoSynchronizer
{
    public static void RefreshLinkedBusinessPartyFields(InvoicePrintModel target, InvoicePrintModel currentDefault)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(currentDefault);

        target.SupplierBusinessNumber = currentDefault.SupplierBusinessNumber;
        target.SupplierName = currentDefault.SupplierName;
        target.SupplierRepresentative = currentDefault.SupplierRepresentative;
        target.SupplierPhone = currentDefault.SupplierPhone;
        target.SupplierAddress = currentDefault.SupplierAddress;

        target.BuyerBusinessNumber = currentDefault.BuyerBusinessNumber;
        target.BuyerName = currentDefault.BuyerName;
        target.BuyerRepresentative = currentDefault.BuyerRepresentative;
        target.BuyerPhone = currentDefault.BuyerPhone;
        target.BuyerAddress = currentDefault.BuyerAddress;

        target.ManagerName = currentDefault.ManagerName;
        target.BankAccountText = currentDefault.BankAccountText;
        target.SupplierStampImage = CloneBytes(currentDefault.SupplierStampImage);
    }

    private static byte[]? CloneBytes(byte[]? bytes)
    {
        if (bytes is null)
            return null;

        return bytes.Length == 0 ? Array.Empty<byte>() : bytes.ToArray();
    }
}
