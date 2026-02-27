using SalesMaster.Shared.Contracts;

namespace SalesMaster.Desktop.App.Data;

/// <summary>
/// Maps between local SQLite entities and shared DTOs.
/// </summary>
public static class LocalMappings
{
    // ── CompanyProfile ──────────────────────────────────────────────────────
    public static LocalCompanyProfile ToLocal(CompanyProfileDto dto) => new()
    {
        Id = dto.Id,
        TradeName = dto.TradeName,
        Representative = dto.Representative,
        BusinessNumber = dto.BusinessNumber,
        BusinessType = dto.BusinessType,
        BusinessItem = dto.BusinessItem,
        Address = dto.Address,
        ContactNumber = dto.ContactNumber,
        Email = dto.Email,
        BankAccountText = dto.BankAccountText,
        StampImage = dto.StampImage,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CompanyProfileDto ToDto(LocalCompanyProfile e) => new()
    {
        Id = e.Id,
        TradeName = e.TradeName,
        Representative = e.Representative,
        BusinessNumber = e.BusinessNumber,
        BusinessType = e.BusinessType,
        BusinessItem = e.BusinessItem,
        Address = e.Address,
        ContactNumber = e.ContactNumber,
        Email = e.Email,
        BankAccountText = e.BankAccountText,
        StampImage = e.StampImage,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── Unit ────────────────────────────────────────────────────────────────
    public static LocalUnit ToLocal(UnitDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        IsActive = dto.IsActive,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static UnitDto ToDto(LocalUnit e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        IsActive = e.IsActive,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── CustomerCategory ────────────────────────────────────────────────────
    public static LocalCustomerCategory ToLocal(CustomerCategoryDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        IsSystemDefault = dto.IsSystemDefault,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CustomerCategoryDto ToDto(LocalCustomerCategory e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        IsSystemDefault = e.IsSystemDefault,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── Customer ────────────────────────────────────────────────────────────
    public static LocalCustomer ToLocal(CustomerDto dto) => new()
    {
        Id = dto.Id,
        CustomerMasterId = dto.CustomerMasterId,
        NameOriginal = dto.NameOriginal,
        NameMatchKey = dto.NameMatchKey,
        CategoryId = dto.CategoryId,
        Department = dto.Department,
        ContactPerson = dto.ContactPerson,
        BusinessNumber = dto.BusinessNumber,
        Address = dto.Address,
        Phone = dto.Phone,
        Email = dto.Email,
        Notes = dto.Notes,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CustomerDto ToDto(LocalCustomer e) => new()
    {
        Id = e.Id,
        CustomerMasterId = e.CustomerMasterId,
        NameOriginal = e.NameOriginal,
        NameMatchKey = e.NameMatchKey,
        CategoryId = e.CategoryId,
        Department = e.Department,
        ContactPerson = e.ContactPerson,
        BusinessNumber = e.BusinessNumber,
        Address = e.Address,
        Phone = e.Phone,
        Email = e.Email,
        Notes = e.Notes,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── Item ────────────────────────────────────────────────────────────────
    public static LocalItem ToLocal(ItemDto dto) => new()
    {
        Id = dto.Id,
        NameOriginal = dto.NameOriginal,
        NameMatchKey = dto.NameMatchKey,
        SpecificationOriginal = dto.SpecificationOriginal,
        SpecificationMatchKey = dto.SpecificationMatchKey,
        Unit = dto.Unit,
        IsRental = dto.IsRental,
        IsSale = dto.IsSale,
        SerialNumber = dto.SerialNumber,
        MaterialNumber = dto.MaterialNumber,
        InstallLocation = dto.InstallLocation,
        RentalStartDate = dto.RentalStartDate,
        RentalEndDate = dto.RentalEndDate,
        Notes = dto.Notes,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static ItemDto ToDto(LocalItem e) => new()
    {
        Id = e.Id,
        NameOriginal = e.NameOriginal,
        NameMatchKey = e.NameMatchKey,
        SpecificationOriginal = e.SpecificationOriginal,
        SpecificationMatchKey = e.SpecificationMatchKey,
        Unit = e.Unit,
        IsRental = e.IsRental,
        IsSale = e.IsSale,
        SerialNumber = e.SerialNumber,
        MaterialNumber = e.MaterialNumber,
        InstallLocation = e.InstallLocation,
        RentalStartDate = e.RentalStartDate,
        RentalEndDate = e.RentalEndDate,
        Notes = e.Notes,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── Invoice ─────────────────────────────────────────────────────────────
    public static LocalInvoice ToLocal(InvoiceDto dto) => new()
    {
        Id = dto.Id,
        CustomerId = dto.CustomerId,
        InvoiceNumber = dto.InvoiceNumber,
        LocalTempNumber = dto.LocalTempNumber,
        VoucherType = dto.VoucherType,
        InvoiceDate = dto.InvoiceDate,
        TotalAmount = dto.TotalAmount,
        SupplyAmount = dto.SupplyAmount,
        VatAmount = dto.VatAmount,
        Memo = dto.Memo,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false,
        Lines = dto.Lines.Select(ToLocal).ToList(),
        Payments = dto.Payments.Select(ToLocal).ToList()
    };

    public static InvoiceDto ToDto(LocalInvoice e) => new()
    {
        Id = e.Id,
        CustomerId = e.CustomerId,
        InvoiceNumber = e.InvoiceNumber,
        LocalTempNumber = e.LocalTempNumber,
        VoucherType = e.VoucherType,
        InvoiceDate = e.InvoiceDate,
        TotalAmount = e.TotalAmount,
        SupplyAmount = e.SupplyAmount,
        VatAmount = e.VatAmount,
        Memo = e.Memo,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted,
        Lines = e.Lines.Where(l => !l.IsDeleted).Select(ToDto).ToList(),
        Payments = e.Payments.Where(p => !p.IsDeleted).Select(ToDto).ToList()
    };

    // ── InvoiceLine ─────────────────────────────────────────────────────────
    public static LocalInvoiceLine ToLocal(InvoiceLineDto dto) => new()
    {
        Id = dto.Id,
        InvoiceId = dto.InvoiceId,
        ItemId = dto.ItemId,
        ItemNameOriginal = dto.ItemNameOriginal,
        SpecificationOriginal = dto.SpecificationOriginal,
        Unit = dto.Unit,
        Quantity = dto.Quantity,
        UnitPrice = dto.UnitPrice,
        LineAmount = dto.LineAmount,
        Remark = dto.Remark,
        SerialNumber = dto.SerialNumber,
        MaterialNumber = dto.MaterialNumber,
        InstallLocation = dto.InstallLocation,
        RentalStartDate = dto.RentalStartDate,
        RentalEndDate = dto.RentalEndDate,
        IsDeleted = dto.IsDeleted
    };

    public static InvoiceLineDto ToDto(LocalInvoiceLine e) => new()
    {
        Id = e.Id,
        InvoiceId = e.InvoiceId,
        ItemId = e.ItemId,
        ItemNameOriginal = e.ItemNameOriginal,
        SpecificationOriginal = e.SpecificationOriginal,
        Unit = e.Unit,
        Quantity = e.Quantity,
        UnitPrice = e.UnitPrice,
        LineAmount = e.LineAmount,
        Remark = e.Remark,
        SerialNumber = e.SerialNumber,
        MaterialNumber = e.MaterialNumber,
        InstallLocation = e.InstallLocation,
        RentalStartDate = e.RentalStartDate,
        RentalEndDate = e.RentalEndDate,
        IsDeleted = e.IsDeleted
    };

    // ── Payment ─────────────────────────────────────────────────────────────
    public static LocalPayment ToLocal(PaymentDto dto) => new()
    {
        Id = dto.Id,
        InvoiceId = dto.InvoiceId,
        PaymentDate = dto.PaymentDate,
        Amount = dto.Amount,
        Note = dto.Note,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static PaymentDto ToDto(LocalPayment e) => new()
    {
        Id = e.Id,
        InvoiceId = e.InvoiceId,
        PaymentDate = e.PaymentDate,
        Amount = e.Amount,
        Note = e.Note,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };
}
