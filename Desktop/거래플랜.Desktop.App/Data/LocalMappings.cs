using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Data;

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
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
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
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── Unit ────────────────────────────────────────────────────────────────
    public static LocalUnit ToLocal(UnitDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        IsActive = dto.IsActive,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static UnitDto ToDto(LocalUnit e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        IsActive = e.IsActive,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── CustomerCategory ────────────────────────────────────────────────────
    public static LocalCustomerCategory ToLocal(CustomerCategoryDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        IsSystemDefault = dto.IsSystemDefault,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CustomerCategoryDto ToDto(LocalCustomerCategory e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        IsSystemDefault = e.IsSystemDefault,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalPriceGradeOption ToLocal(PriceGradeOptionDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        PriceSource = dto.PriceSource,
        SortOrder = dto.SortOrder,
        IsSystemDefault = dto.IsSystemDefault,
        IsActive = dto.IsActive,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static PriceGradeOptionDto ToDto(LocalPriceGradeOption e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        PriceSource = e.PriceSource,
        SortOrder = e.SortOrder,
        IsSystemDefault = e.IsSystemDefault,
        IsActive = e.IsActive,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalTradeTypeOption ToLocal(TradeTypeOptionDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        AllowsSales = dto.AllowsSales,
        AllowsPurchase = dto.AllowsPurchase,
        SortOrder = dto.SortOrder,
        IsSystemDefault = dto.IsSystemDefault,
        IsActive = dto.IsActive,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static TradeTypeOptionDto ToDto(LocalTradeTypeOption e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        AllowsSales = e.AllowsSales,
        AllowsPurchase = e.AllowsPurchase,
        SortOrder = e.SortOrder,
        IsSystemDefault = e.IsSystemDefault,
        IsActive = e.IsActive,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalItemCategoryOption ToLocal(ItemCategoryOptionDto dto) => new()
    {
        Id = dto.Id,
        Name = dto.Name,
        SortOrder = dto.SortOrder,
        IsSystemDefault = dto.IsSystemDefault,
        IsActive = dto.IsActive,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static ItemCategoryOptionDto ToDto(LocalItemCategoryOption e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        SortOrder = e.SortOrder,
        IsSystemDefault = e.IsSystemDefault,
        IsActive = e.IsActive,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalCustomerMaster ToLocal(CustomerMasterDto dto) => new()
    {
        Id = dto.Id,
        NameOriginal = dto.NameOriginal,
        NameMatchKey = dto.NameMatchKey,
        CategoryId = dto.CategoryId,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CustomerMasterDto ToDto(LocalCustomerMaster e) => new()
    {
        Id = e.Id,
        NameOriginal = e.NameOriginal,
        NameMatchKey = e.NameMatchKey,
        CategoryId = e.CategoryId,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
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
        TradeType = CustomerTradeTypes.Normalize(dto.TradeType),
        Department = dto.Department,
        ContactPerson = dto.ContactPerson,
        BusinessNumber = dto.BusinessNumber,
        Address = dto.Address,
        Phone = dto.Phone,
        Email = dto.Email,
        Notes = dto.Notes,
        ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, DomainConstants.OfficeUsenet),
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CustomerDto ToDto(LocalCustomer e) => new()
    {
        Id = e.Id,
        CustomerMasterId = e.CustomerMasterId,
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        NameOriginal = e.NameOriginal,
        NameMatchKey = e.NameMatchKey,
        CategoryId = e.CategoryId,
        TradeType = CustomerTradeTypes.Normalize(e.TradeType),
        Department = e.Department,
        ContactPerson = e.ContactPerson,
        BusinessNumber = e.BusinessNumber,
        Address = e.Address,
        Phone = e.Phone,
        Email = e.Email,
        Notes = e.Notes,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalCustomerContract ToLocal(CustomerContractDto dto) => new()
    {
        Id = dto.Id,
        CustomerId = dto.CustomerId,
        ContractType = dto.ContractType,
        FileName = dto.FileName,
        MimeType = dto.MimeType,
        FileSize = dto.FileSize,
        FileHash = dto.FileHash,
        Description = dto.Description,
        SignedDate = dto.SignedDate,
        ExpireDate = dto.ExpireDate,
        IsPrimary = dto.IsPrimary,
        UploadedByUsername = dto.UploadedByUsername,
        UploadedAtUtc = dto.UploadedAtUtc,
        FileContent = dto.FileContent ?? [],
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static CustomerContractDto ToDto(LocalCustomerContract e) => new()
    {
        Id = e.Id,
        CustomerId = e.CustomerId,
        ContractType = e.ContractType,
        FileName = e.FileName,
        MimeType = e.MimeType,
        FileSize = e.FileSize,
        FileHash = e.FileHash,
        Description = e.Description,
        SignedDate = e.SignedDate,
        ExpireDate = e.ExpireDate,
        IsPrimary = e.IsPrimary,
        UploadedByUsername = e.UploadedByUsername,
        UploadedAtUtc = e.UploadedAtUtc,
        FileContent = e.FileContent ?? [],
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
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
        CategoryName = dto.CategoryName,
        Unit = dto.Unit,
        CurrentStock = dto.CurrentStock,
        SafetyStock = dto.SafetyStock,
        PurchasePrice = dto.PurchasePrice,
        SalePrice = dto.SalePrice,
        RetailPrice = dto.RetailPrice,
        PriceGradeA = dto.PriceGradeA,
        PriceGradeB = dto.PriceGradeB,
        PriceGradeC = dto.PriceGradeC,
        SimpleMemo = dto.SimpleMemo,
        IsRental = dto.IsRental,
        IsSale = dto.IsSale,
        SerialNumber = dto.SerialNumber,
        MaterialNumber = dto.MaterialNumber,
        InstallLocation = dto.InstallLocation,
        RentalStartDate = dto.RentalStartDate,
        RentalEndDate = dto.RentalEndDate,
        Notes = dto.Notes,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
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
        CategoryName = e.CategoryName,
        Unit = e.Unit,
        CurrentStock = e.CurrentStock,
        SafetyStock = e.SafetyStock,
        PurchasePrice = e.PurchasePrice,
        SalePrice = e.SalePrice,
        RetailPrice = e.RetailPrice,
        PriceGradeA = e.PriceGradeA,
        PriceGradeB = e.PriceGradeB,
        PriceGradeC = e.PriceGradeC,
        SimpleMemo = e.SimpleMemo,
        IsRental = e.IsRental,
        IsSale = e.IsSale,
        SerialNumber = e.SerialNumber,
        MaterialNumber = e.MaterialNumber,
        InstallLocation = e.InstallLocation,
        RentalStartDate = e.RentalStartDate,
        RentalEndDate = e.RentalEndDate,
        Notes = e.Notes,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    // ── Invoice ─────────────────────────────────────────────────────────────
    public static LocalInvoice ToLocal(InvoiceDto dto) => new()
    {
        Id = dto.Id,
        CustomerId = dto.CustomerId,
        ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, DomainConstants.OfficeUsenet),
        InvoiceNumber = dto.InvoiceNumber,
        LocalTempNumber = dto.LocalTempNumber,
        VoucherType = dto.VoucherType,
        InvoiceDate = dto.InvoiceDate,
        TotalAmount = dto.TotalAmount,
        SupplyAmount = dto.SupplyAmount,
        VatAmount = dto.VatAmount,
        Memo = dto.Memo,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
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
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        InvoiceNumber = e.InvoiceNumber,
        LocalTempNumber = e.LocalTempNumber,
        VoucherType = e.VoucherType,
        InvoiceDate = e.InvoiceDate,
        TotalAmount = e.TotalAmount,
        SupplyAmount = e.SupplyAmount,
        VatAmount = e.VatAmount,
        Memo = e.Memo,
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
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
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
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
        CreatedAtUtc = e.CreatedAtUtc,
        UpdatedAtUtc = e.UpdatedAtUtc,
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalItemWarehouseStock ToLocal(ItemWarehouseStockDto dto) => new()
    {
        ItemId = dto.ItemId,
        WarehouseCode = dto.WarehouseCode,
        Quantity = dto.Quantity,
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc)
    };

    public static ItemWarehouseStockDto ToDto(LocalItemWarehouseStock e) => new()
    {
        ItemId = e.ItemId,
        WarehouseCode = e.WarehouseCode,
        Quantity = e.Quantity,
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc)
    };

    private static DateTime EnsureUtc(DateTime value)
    {
        if (value == default)
            return DateTime.UtcNow;

        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
