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
        Name = UnitCatalogNormalizer.Normalize(dto.Name),
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
        Name = UnitCatalogNormalizer.Normalize(e.Name),
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
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, dto.OfficeCode),
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, OfficeCodeCatalog.Shared),
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
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(e.TenantCode, e.OfficeCode),
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.OfficeCode, OfficeCodeCatalog.Shared),
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
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, dto.OfficeCode),
        NameOriginal = dto.NameOriginal,
        NameMatchKey = dto.NameMatchKey,
        CategoryId = dto.CategoryId,
        TradeType = CustomerTradeTypes.Normalize(dto.TradeType),
        Department = dto.Department,
        ContactPerson = dto.ContactPerson,
        Representative = dto.Representative,
        BusinessNumber = dto.BusinessNumber,
        BusinessType = dto.BusinessType,
        BusinessItem = dto.BusinessItem,
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
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(e.TenantCode, e.ResponsibleOfficeCode),
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        NameOriginal = e.NameOriginal,
        NameMatchKey = e.NameMatchKey,
        CategoryId = e.CategoryId,
        TradeType = CustomerTradeTypes.Normalize(e.TradeType),
        Department = e.Department,
        ContactPerson = e.ContactPerson,
        Representative = e.Representative,
        BusinessNumber = e.BusinessNumber,
        BusinessType = e.BusinessType,
        BusinessItem = e.BusinessItem,
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
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, dto.OfficeCode),
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, OfficeCodeCatalog.Shared),
        NameOriginal = dto.NameOriginal,
        NameMatchKey = dto.NameMatchKey,
        SpecificationOriginal = dto.SpecificationOriginal,
        SpecificationMatchKey = dto.SpecificationMatchKey,
        CategoryName = dto.CategoryName,
        ItemKind = ItemOperationalPolicy.NormalizeItemKind(dto.ItemKind, dto.TrackingType, dto.CategoryName, dto.IsRental),
        TrackingType = ItemOperationalPolicy.NormalizeTrackingType(dto.TrackingType, dto.ItemKind, dto.CategoryName, dto.IsRental),
        Unit = UnitCatalogNormalizer.Normalize(dto.Unit),
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
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(e.TenantCode, e.OfficeCode),
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.OfficeCode, OfficeCodeCatalog.Shared),
        NameOriginal = e.NameOriginal,
        NameMatchKey = e.NameMatchKey,
        SpecificationOriginal = e.SpecificationOriginal,
        SpecificationMatchKey = e.SpecificationMatchKey,
        CategoryName = e.CategoryName,
        ItemKind = ItemOperationalPolicy.NormalizeItemKind(e.ItemKind, e.TrackingType, e.CategoryName, e.IsRental),
        TrackingType = ItemOperationalPolicy.NormalizeTrackingType(e.TrackingType, e.ItemKind, e.CategoryName, e.IsRental),
        Unit = UnitCatalogNormalizer.Normalize(e.Unit),
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
        VersionGroupId = dto.VersionGroupId == Guid.Empty ? dto.Id : dto.VersionGroupId,
        VersionNumber = dto.VersionNumber <= 0 ? 1 : dto.VersionNumber,
        PreviousVersionId = dto.PreviousVersionId,
        IsLatestVersion = dto.IsLatestVersion,
        VoucherType = dto.VoucherType,
        InvoiceDate = dto.InvoiceDate,
        TotalAmount = dto.TotalAmount,
        SupplyAmount = dto.SupplyAmount,
        VatAmount = dto.VatAmount,
        TaxInvoiceIssued = dto.TaxInvoiceIssued,
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
        CustomerName = string.Empty,
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        InvoiceNumber = e.InvoiceNumber,
        LocalTempNumber = e.LocalTempNumber,
        VersionGroupId = e.VersionGroupId == Guid.Empty ? e.Id : e.VersionGroupId,
        VersionNumber = e.VersionNumber <= 0 ? 1 : e.VersionNumber,
        PreviousVersionId = e.PreviousVersionId,
        IsLatestVersion = e.IsLatestVersion,
        VoucherType = e.VoucherType,
        InvoiceDate = e.InvoiceDate,
        TotalAmount = e.TotalAmount,
        SupplyAmount = e.SupplyAmount,
        VatAmount = e.VatAmount,
        TaxInvoiceIssued = e.TaxInvoiceIssued,
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
        Unit = UnitCatalogNormalizer.Normalize(dto.Unit),
        Quantity = dto.Quantity,
        UnitPrice = dto.UnitPrice,
        LineAmount = dto.LineAmount,
        Remark = dto.Remark,
        SerialNumber = dto.SerialNumber,
        MaterialNumber = dto.MaterialNumber,
        InstallLocation = dto.InstallLocation,
        RentalStartDate = dto.RentalStartDate,
        RentalEndDate = dto.RentalEndDate,
        ItemTrackingType = ItemTrackingTypes.Normalize(dto.ItemTrackingType),
        IsDeleted = dto.IsDeleted
    };

    public static InvoiceLineDto ToDto(LocalInvoiceLine e) => new()
    {
        Id = e.Id,
        InvoiceId = e.InvoiceId,
        ItemId = e.ItemId,
        ItemNameOriginal = e.ItemNameOriginal,
        SpecificationOriginal = e.SpecificationOriginal,
        Unit = UnitCatalogNormalizer.Normalize(e.Unit),
        Quantity = e.Quantity,
        UnitPrice = e.UnitPrice,
        LineAmount = e.LineAmount,
        Remark = e.Remark,
        SerialNumber = e.SerialNumber,
        MaterialNumber = e.MaterialNumber,
        InstallLocation = e.InstallLocation,
        RentalStartDate = e.RentalStartDate,
        RentalEndDate = e.RentalEndDate,
        ItemTrackingType = ItemTrackingTypes.Normalize(e.ItemTrackingType),
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

    public static LocalTransaction ToLocal(TransactionDto dto) => new()
    {
        Id = dto.Id,
        CustomerId = dto.CustomerId,
        ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, DomainConstants.OfficeUsenet),
        TransactionDate = dto.TransactionDate,
        TransactionKind = dto.TransactionKind,
        LinkedInvoiceId = dto.LinkedInvoiceId,
        LinkedInvoiceNumber = dto.LinkedInvoiceNumber,
        LinkedRentalBillingProfileId = dto.LinkedRentalBillingProfileId,
        LinkedRentalBillingRunId = dto.LinkedRentalBillingRunId,
        SettlementAmount = dto.SettlementAmount,
        AdvanceDelta = dto.AdvanceDelta,
        PrepaidDelta = dto.PrepaidDelta,
        CashReceipt = dto.CashReceipt,
        CardReceipt = dto.CardReceipt,
        BankReceipt = dto.BankReceipt,
        DiscountApplied = dto.DiscountApplied,
        ReceiptTotal = dto.ReceiptTotal,
        CashPayment = dto.CashPayment,
        CardPayment = dto.CardPayment,
        BankPayment = dto.BankPayment,
        DiscountReceived = dto.DiscountReceived,
        PaymentTotal = dto.PaymentTotal,
        Note = dto.Note,
        Memo = dto.Memo,
        CreatedAtUtc = dto.CreatedAtUtc,
        UpdatedAtUtc = dto.UpdatedAtUtc,
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static TransactionDto ToDto(LocalTransaction e) => new()
    {
        Id = e.Id,
        CustomerId = e.CustomerId,
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        TransactionDate = e.TransactionDate,
        TransactionKind = e.TransactionKind,
        LinkedInvoiceId = e.LinkedInvoiceId,
        LinkedInvoiceNumber = e.LinkedInvoiceNumber,
        LinkedRentalBillingProfileId = e.LinkedRentalBillingProfileId,
        LinkedRentalBillingRunId = e.LinkedRentalBillingRunId,
        SettlementAmount = e.SettlementAmount,
        AdvanceDelta = e.AdvanceDelta,
        PrepaidDelta = e.PrepaidDelta,
        CashReceipt = e.CashReceipt,
        CardReceipt = e.CardReceipt,
        BankReceipt = e.BankReceipt,
        DiscountApplied = e.DiscountApplied,
        ReceiptTotal = e.ReceiptTotal,
        CashPayment = e.CashPayment,
        CardPayment = e.CardPayment,
        BankPayment = e.BankPayment,
        DiscountReceived = e.DiscountReceived,
        PaymentTotal = e.PaymentTotal,
        Note = e.Note,
        Memo = e.Memo,
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalTransactionAttachment ToLocal(TransactionAttachmentDto dto, string? storedFileName = null, string? storedPath = null) => new()
    {
        Id = dto.Id,
        TransactionId = dto.TransactionId,
        AttachmentType = dto.AttachmentType,
        FileName = dto.FileName,
        StoredFileName = storedFileName ?? dto.FileName,
        StoredPath = storedPath ?? string.Empty,
        MimeType = dto.MimeType,
        FileSize = dto.FileSize,
        FileHash = dto.FileHash,
        Description = dto.Description,
        UploadedByUsername = dto.UploadedByUsername,
        UploadedAtUtc = EnsureUtc(dto.UploadedAtUtc),
        VerificationStatus = dto.VerificationStatus,
        VerifiedByUsername = dto.VerifiedByUsername,
        VerifiedAtUtc = dto.VerifiedAtUtc.HasValue ? EnsureUtc(dto.VerifiedAtUtc.Value) : null,
        VerificationMemo = dto.VerificationMemo,
        SortOrder = dto.SortOrder,
        CreatedAtUtc = EnsureUtc(dto.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc),
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static TransactionAttachmentDto ToDto(LocalTransactionAttachment e, byte[]? fileContent = null) => new()
    {
        Id = e.Id,
        TransactionId = e.TransactionId,
        AttachmentType = e.AttachmentType,
        FileName = e.FileName,
        MimeType = e.MimeType,
        FileSize = e.FileSize,
        FileHash = e.FileHash,
        Description = e.Description,
        UploadedByUsername = e.UploadedByUsername,
        UploadedAtUtc = EnsureUtc(e.UploadedAtUtc),
        VerificationStatus = e.VerificationStatus,
        VerifiedByUsername = e.VerifiedByUsername,
        VerifiedAtUtc = e.VerifiedAtUtc.HasValue ? EnsureUtc(e.VerifiedAtUtc.Value) : null,
        VerificationMemo = e.VerificationMemo,
        SortOrder = e.SortOrder,
        FileContent = fileContent ?? [],
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalInventoryTransfer ToLocal(InventoryTransferDto dto) => new()
    {
        Id = dto.Id,
        TransferNumber = dto.TransferNumber,
        TransferDate = dto.TransferDate,
        FromWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(dto.FromWarehouseCode, dto.SourceOfficeCode, DomainConstants.OfficeUsenet),
        ToWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(dto.ToWarehouseCode, dto.TargetOfficeCode, DomainConstants.OfficeYeonsu),
        Memo = dto.Memo,
        CreatedByUsername = dto.CreatedByUsername,
        LastSavedByUsername = dto.LastSavedByUsername,
        LastSavedAtUtc = EnsureUtc(dto.LastSavedAtUtc),
        TransferStatus = InventoryTransferStatusNormalizer.Normalize(dto.TransferStatus, dto.ReceivedByUsername, dto.ReceivedAtUtc, dto.RejectedByUsername, dto.RejectedAtUtc),
        RequestedByUsername = dto.RequestedByUsername,
        RequestedAtUtc = dto.RequestedAtUtc.HasValue ? EnsureUtc(dto.RequestedAtUtc.Value) : null,
        ReceivedByUsername = dto.ReceivedByUsername,
        ReceivedAtUtc = dto.ReceivedAtUtc.HasValue ? EnsureUtc(dto.ReceivedAtUtc.Value) : null,
        ReceiveMemo = dto.ReceiveMemo,
        ReceiveEvidencePath = dto.ReceiveEvidencePath,
        RejectedByUsername = dto.RejectedByUsername,
        RejectedAtUtc = dto.RejectedAtUtc.HasValue ? EnsureUtc(dto.RejectedAtUtc.Value) : null,
        RejectReason = dto.RejectReason,
        LastStatusChangedByUsername = dto.LastStatusChangedByUsername,
        LastStatusChangedAtUtc = dto.LastStatusChangedAtUtc.HasValue ? EnsureUtc(dto.LastStatusChangedAtUtc.Value) : null,
        CreatedAtUtc = EnsureUtc(dto.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc),
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false,
        Lines = (dto.Lines ?? []).Select(ToLocal).ToList()
    };

    public static InventoryTransferDto ToDto(LocalInventoryTransfer e) => new()
    {
        Id = e.Id,
        TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            null,
            OfficeCodeCatalog.NormalizeOfficeCodeLoose(null, e.FromWarehouseCode, DomainConstants.OfficeUsenet)),
        SourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(null, e.FromWarehouseCode, DomainConstants.OfficeUsenet),
        TargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeLoose(null, e.ToWarehouseCode, DomainConstants.OfficeYeonsu),
        TransferNumber = e.TransferNumber,
        TransferDate = e.TransferDate,
        FromWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(e.FromWarehouseCode, null, DomainConstants.OfficeUsenet),
        ToWarehouseCode = OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(e.ToWarehouseCode, null, DomainConstants.OfficeYeonsu),
        Memo = e.Memo,
        CreatedByUsername = e.CreatedByUsername,
        LastSavedByUsername = e.LastSavedByUsername,
        LastSavedAtUtc = EnsureUtc(e.LastSavedAtUtc),
        TransferStatus = InventoryTransferStatusNormalizer.Normalize(e.TransferStatus, e.ReceivedByUsername, e.ReceivedAtUtc, e.RejectedByUsername, e.RejectedAtUtc),
        RequestedByUsername = e.RequestedByUsername,
        RequestedAtUtc = e.RequestedAtUtc.HasValue ? EnsureUtc(e.RequestedAtUtc.Value) : null,
        ReceivedByUsername = e.ReceivedByUsername,
        ReceivedAtUtc = e.ReceivedAtUtc.HasValue ? EnsureUtc(e.ReceivedAtUtc.Value) : null,
        ReceiveMemo = e.ReceiveMemo,
        ReceiveEvidencePath = e.ReceiveEvidencePath,
        RejectedByUsername = e.RejectedByUsername,
        RejectedAtUtc = e.RejectedAtUtc.HasValue ? EnsureUtc(e.RejectedAtUtc.Value) : null,
        RejectReason = e.RejectReason,
        LastStatusChangedByUsername = e.LastStatusChangedByUsername,
        LastStatusChangedAtUtc = e.LastStatusChangedAtUtc.HasValue ? EnsureUtc(e.LastStatusChangedAtUtc.Value) : null,
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted,
        Lines = e.Lines.Where(line => !line.IsDeleted).Select(ToDto).ToList()
    };

    public static LocalInventoryTransferLine ToLocal(InventoryTransferLineDto dto) => new()
    {
        Id = dto.Id,
        TransferId = dto.TransferId,
        ItemId = dto.ItemId,
        ItemNameOriginal = dto.ItemNameOriginal,
        SpecificationOriginal = dto.SpecificationOriginal,
        Unit = UnitCatalogNormalizer.Normalize(dto.Unit),
        Quantity = dto.Quantity,
        ReceivedQuantity = dto.ReceivedQuantity,
        QuantityDifference = dto.QuantityDifference,
        Remark = dto.Remark,
        ReceiptRemark = dto.ReceiptRemark,
        IsDeleted = dto.IsDeleted
    };

    public static InventoryTransferLineDto ToDto(LocalInventoryTransferLine e) => new()
    {
        Id = e.Id,
        TransferId = e.TransferId,
        ItemId = e.ItemId,
        ItemNameOriginal = e.ItemNameOriginal,
        SpecificationOriginal = e.SpecificationOriginal,
        Unit = UnitCatalogNormalizer.Normalize(e.Unit),
        Quantity = e.Quantity,
        ReceivedQuantity = e.ReceivedQuantity,
        QuantityDifference = e.QuantityDifference,
        Remark = e.Remark,
        ReceiptRemark = e.ReceiptRemark,
        IsDeleted = e.IsDeleted
    };

    public static LocalRentalManagementCompany ToLocal(RentalManagementCompanyDto dto) => new()
    {
        Id = dto.Id,
        Code = dto.Code,
        Name = dto.Name,
        IsSystemDefault = dto.IsSystemDefault,
        IsActive = dto.IsActive,
        CreatedAtUtc = EnsureUtc(dto.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc),
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static RentalManagementCompanyDto ToDto(LocalRentalManagementCompany e) => new()
    {
        Id = e.Id,
        Code = e.Code,
        Name = e.Name,
        IsSystemDefault = e.IsSystemDefault,
        IsActive = e.IsActive,
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalRentalBillingProfile ToLocal(RentalBillingProfileDto dto) => new()
    {
        Id = dto.Id,
        ProfileKey = dto.ProfileKey,
        CustomerId = dto.CustomerId,
        CustomerName = dto.CustomerName,
        BusinessNumber = dto.BusinessNumber,
        RealCustomerName = dto.RealCustomerName,
        ItemName = dto.ItemName,
        BillingType = dto.BillingType,
        BillToCustomerName = dto.BillToCustomerName,
        InstallSiteName = dto.InstallSiteName,
        BillingAdvanceMode = dto.BillingAdvanceMode,
        ManagementCompanyCode = dto.ManagementCompanyCode,
        BillingMethod = dto.BillingMethod,
        PaymentMethod = dto.PaymentMethod,
        BillingStatus = dto.BillingStatus,
        Email = dto.Email,
        BillingDay = dto.BillingDay,
        BillingCycleMonths = dto.BillingCycleMonths,
        MonthlyAmount = dto.MonthlyAmount,
        DepositAmount = dto.DepositAmount,
        SubmissionDocuments = dto.SubmissionDocuments,
        Notes = dto.Notes,
        BillingAnchorDate = dto.BillingAnchorDate,
        BillingStartDate = dto.BillingStartDate,
        ContractDate = dto.ContractDate,
        ContractStartDate = dto.ContractStartDate,
        ContractEndDate = dto.ContractEndDate,
        LastBilledDate = dto.LastBilledDate,
        SettlementStatus = dto.SettlementStatus,
        CompletionStatus = dto.CompletionStatus,
        SettledAmount = dto.SettledAmount,
        OutstandingAmount = dto.OutstandingAmount,
        RequiresFollowUp = dto.RequiresFollowUp,
        FollowUpNote = dto.FollowUpNote,
        LastSettledDate = dto.LastSettledDate,
        ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, DomainConstants.OfficeUsenet),
        AssignedUsername = dto.AssignedUsername,
        BillingTemplateJson = dto.BillingTemplateJson,
        BillingRunsJson = dto.BillingRunsJson,
        IsActive = dto.IsActive,
        CreatedAtUtc = EnsureUtc(dto.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc),
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static RentalBillingProfileDto ToDto(LocalRentalBillingProfile e) => new()
    {
        Id = e.Id,
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        ProfileKey = e.ProfileKey,
        CustomerId = e.CustomerId,
        CustomerName = e.CustomerName,
        BusinessNumber = e.BusinessNumber,
        RealCustomerName = e.RealCustomerName,
        ItemName = e.ItemName,
        BillingType = e.BillingType,
        BillToCustomerName = e.BillToCustomerName,
        InstallSiteName = e.InstallSiteName,
        BillingAdvanceMode = e.BillingAdvanceMode,
        ManagementCompanyCode = e.ManagementCompanyCode,
        BillingMethod = e.BillingMethod,
        PaymentMethod = e.PaymentMethod,
        BillingStatus = e.BillingStatus,
        Email = e.Email,
        BillingDay = e.BillingDay,
        BillingCycleMonths = e.BillingCycleMonths,
        MonthlyAmount = e.MonthlyAmount,
        DepositAmount = e.DepositAmount,
        SubmissionDocuments = e.SubmissionDocuments,
        Notes = e.Notes,
        BillingAnchorDate = e.BillingAnchorDate,
        BillingStartDate = e.BillingStartDate,
        ContractDate = e.ContractDate,
        ContractStartDate = e.ContractStartDate,
        ContractEndDate = e.ContractEndDate,
        LastBilledDate = e.LastBilledDate,
        SettlementStatus = e.SettlementStatus,
        CompletionStatus = e.CompletionStatus,
        SettledAmount = e.SettledAmount,
        OutstandingAmount = e.OutstandingAmount,
        RequiresFollowUp = e.RequiresFollowUp,
        FollowUpNote = e.FollowUpNote,
        LastSettledDate = e.LastSettledDate,
        AssignedUsername = e.AssignedUsername,
        BillingTemplateJson = e.BillingTemplateJson,
        BillingRunsJson = e.BillingRunsJson,
        IsActive = e.IsActive,
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalRentalAsset ToLocal(RentalAssetDto dto) => new()
    {
        Id = dto.Id,
        AssetKey = dto.AssetKey,
        CustomerId = dto.CustomerId,
        ItemId = dto.ItemId,
        BillingProfileId = dto.BillingProfileId,
        ManagementId = dto.ManagementId,
        ManagementNumber = dto.ManagementNumber,
        ManagementCompanyCode = dto.ManagementCompanyCode,
        CurrentLocation = dto.CurrentLocation,
        CurrentCustomerName = dto.CurrentCustomerName,
        BillToCustomerName = dto.BillToCustomerName,
        InstallSiteName = dto.InstallSiteName,
        BillingEligibilityStatus = dto.BillingEligibilityStatus,
        BillingExclusionReason = dto.BillingExclusionReason,
        ItemCategoryName = dto.ItemCategoryName,
        Manufacturer = dto.Manufacturer,
        ItemName = dto.ItemName,
        MachineNumber = dto.MachineNumber,
        PurchaseVendor = dto.PurchaseVendor,
        PurchaseDate = dto.PurchaseDate,
        DisposalDate = dto.DisposalDate,
        PurchasePrice = dto.PurchasePrice,
        SalePrice = dto.SalePrice,
        CustomerName = dto.CustomerName,
        InstallLocation = dto.InstallLocation,
        DepositText = dto.DepositText,
        MonthlyFee = dto.MonthlyFee,
        ContractMonths = dto.ContractMonths,
        ContractDate = dto.ContractDate,
        InstallDate = dto.InstallDate,
        ContractStartDate = dto.ContractStartDate,
        RentalEndDate = dto.RentalEndDate,
        FreeSupplyItems = dto.FreeSupplyItems,
        PaidSupplyItems = dto.PaidSupplyItems,
        ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, DomainConstants.OfficeUsenet),
        AssignedUsername = dto.AssignedUsername,
        AssetStatus = dto.AssetStatus,
        Notes = dto.Notes,
        CreatedAtUtc = EnsureUtc(dto.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc),
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static RentalAssetDto ToDto(LocalRentalAsset e) => new()
    {
        Id = e.Id,
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        AssetKey = e.AssetKey,
        CustomerId = e.CustomerId,
        ItemId = e.ItemId,
        BillingProfileId = e.BillingProfileId,
        ManagementId = e.ManagementId,
        ManagementNumber = e.ManagementNumber,
        ManagementCompanyCode = e.ManagementCompanyCode,
        CurrentLocation = e.CurrentLocation,
        CurrentCustomerName = e.CurrentCustomerName,
        BillToCustomerName = e.BillToCustomerName,
        InstallSiteName = e.InstallSiteName,
        BillingEligibilityStatus = e.BillingEligibilityStatus,
        BillingExclusionReason = e.BillingExclusionReason,
        ItemCategoryName = e.ItemCategoryName,
        Manufacturer = e.Manufacturer,
        ItemName = e.ItemName,
        MachineNumber = e.MachineNumber,
        PurchaseVendor = e.PurchaseVendor,
        PurchaseDate = e.PurchaseDate,
        DisposalDate = e.DisposalDate,
        PurchasePrice = e.PurchasePrice,
        SalePrice = e.SalePrice,
        CustomerName = e.CustomerName,
        InstallLocation = e.InstallLocation,
        DepositText = e.DepositText,
        MonthlyFee = e.MonthlyFee,
        ContractMonths = e.ContractMonths,
        ContractDate = e.ContractDate,
        InstallDate = e.InstallDate,
        ContractStartDate = e.ContractStartDate,
        RentalEndDate = e.RentalEndDate,
        FreeSupplyItems = e.FreeSupplyItems,
        PaidSupplyItems = e.PaidSupplyItems,
        AssignedUsername = e.AssignedUsername,
        AssetStatus = e.AssetStatus,
        Notes = e.Notes,
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
    };

    public static LocalRentalBillingLog ToLocal(RentalBillingLogDto dto) => new()
    {
        Id = dto.Id,
        BillingProfileId = dto.BillingProfileId,
        BillingYearMonth = dto.BillingYearMonth,
        ScheduledDate = dto.ScheduledDate,
        ProcessedDate = dto.ProcessedDate,
        ProcessedByUsername = dto.ProcessedByUsername,
        Status = dto.Status,
        BilledAmount = dto.BilledAmount,
        Note = dto.Note,
        ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, DomainConstants.OfficeUsenet),
        AssignedUsername = dto.AssignedUsername,
        CreatedAtUtc = EnsureUtc(dto.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(dto.UpdatedAtUtc),
        Revision = dto.Revision,
        IsDeleted = dto.IsDeleted,
        IsDirty = false
    };

    public static RentalBillingLogDto ToDto(LocalRentalBillingLog e) => new()
    {
        Id = e.Id,
        OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(e.ResponsibleOfficeCode, DomainConstants.OfficeUsenet),
        BillingProfileId = e.BillingProfileId,
        BillingYearMonth = e.BillingYearMonth,
        ScheduledDate = e.ScheduledDate,
        ProcessedDate = e.ProcessedDate,
        ProcessedByUsername = e.ProcessedByUsername,
        Status = e.Status,
        BilledAmount = e.BilledAmount,
        Note = e.Note,
        AssignedUsername = e.AssignedUsername,
        CreatedAtUtc = EnsureUtc(e.CreatedAtUtc),
        UpdatedAtUtc = EnsureUtc(e.UpdatedAtUtc),
        Revision = e.Revision,
        IsDeleted = e.IsDeleted
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
