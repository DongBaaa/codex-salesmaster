using 거래플랜.Server.Api.Domain;
using 거래플랜.Server.Api.Utilities;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Mappings;

public static class DtoMappings
{
    public static UserAccountDto ToDto(this UserAccount entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            Username = entity.Username,
            Role = entity.Role,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.OfficeCode),
            ScopeType = entity.Role.Equals("Admin", StringComparison.OrdinalIgnoreCase)
                ? TenantScopeCatalog.ScopeAdmin
                : TenantScopeCatalog.NormalizeScopeTypeOrDefault(entity.ScopeType),
            IsActive = entity.IsActive,
            Permissions = entity.Permissions.Select(x => x.Permission).Distinct().OrderBy(x => x).ToList()
        };

    public static CompanyProfileDto ToDto(this CompanyProfile entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            TradeName = entity.TradeName, Representative = entity.Representative,
            BusinessNumber = entity.BusinessNumber, BusinessType = entity.BusinessType,
            BusinessItem = entity.BusinessItem, Address = entity.Address,
            ContactNumber = entity.ContactNumber, Email = entity.Email,
            BankAccountText = entity.BankAccountText, StampImage = entity.StampImage
        };

    public static TenantDefinitionDto ToDto(this TenantDefinition entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(entity.TenantCode),
            DisplayName = entity.DisplayName,
            StorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(entity.StorageMode),
            Description = entity.Description,
            IsActive = entity.IsActive
        };

    public static TenantOfficeDefinitionDto ToDto(this TenantOfficeDefinition entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.OfficeCode),
            DisplayName = entity.DisplayName,
            IsHeadOffice = entity.IsHeadOffice,
            IsActive = entity.IsActive
        };

    public static DataSharingPolicyDto ToDto(this DataSharingPolicy entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            SourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.SourceTenantCode, entity.SourceOfficeCode),
            SourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.SourceOfficeCode),
            TargetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TargetTenantCode, entity.TargetOfficeCode),
            TargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.TargetOfficeCode),
            ShareCustomers = entity.ShareCustomers,
            ShareItems = entity.ShareItems,
            ShareInvoices = entity.ShareInvoices,
            SharePayments = entity.SharePayments,
            ShareContracts = entity.ShareContracts,
            ShareReports = entity.ShareReports,
            ShareRentals = entity.ShareRentals,
            ShareDeliveries = entity.ShareDeliveries,
            AllowTargetWrite = entity.AllowTargetWrite,
            Note = entity.Note,
            IsActive = entity.IsActive
        };

    public static void Apply(this CompanyProfile entity, CompanyProfileDto dto)
    {
        entity.TradeName = dto.TradeName; entity.Representative = dto.Representative;
        entity.BusinessNumber = dto.BusinessNumber; entity.BusinessType = dto.BusinessType;
        entity.BusinessItem = dto.BusinessItem; entity.Address = dto.Address;
        entity.ContactNumber = dto.ContactNumber; entity.Email = dto.Email;
        entity.BankAccountText = dto.BankAccountText; entity.StampImage = dto.StampImage;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static void Apply(this TenantDefinition entity, TenantDefinitionDto dto)
    {
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(dto.TenantCode, entity.TenantCode);
        entity.DisplayName = dto.DisplayName?.Trim() ?? string.Empty;
        entity.StorageMode = TenantScopeCatalog.NormalizeStorageModeOrDefault(dto.StorageMode, entity.StorageMode);
        entity.Description = dto.Description?.Trim() ?? string.Empty;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static void Apply(this TenantOfficeDefinition entity, TenantOfficeDefinitionDto dto)
    {
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TenantCode, dto.OfficeCode, entity.TenantCode, entity.OfficeCode);
        entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.OfficeCode, entity.OfficeCode);
        entity.DisplayName = dto.DisplayName?.Trim() ?? string.Empty;
        entity.IsHeadOffice = dto.IsHeadOffice;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static void Apply(this DataSharingPolicy entity, DataSharingPolicyDto dto)
    {
        entity.SourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.SourceOfficeCode, entity.SourceOfficeCode);
        entity.SourceTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.SourceTenantCode, entity.SourceOfficeCode);
        entity.TargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.TargetOfficeCode, entity.TargetOfficeCode);
        entity.TargetTenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(dto.TargetTenantCode, entity.TargetOfficeCode);
        entity.ShareCustomers = dto.ShareCustomers;
        entity.ShareItems = dto.ShareItems;
        entity.ShareInvoices = dto.ShareInvoices;
        entity.SharePayments = dto.SharePayments;
        entity.ShareContracts = dto.ShareContracts;
        entity.ShareReports = dto.ShareReports;
        entity.ShareRentals = dto.ShareRentals;
        entity.ShareDeliveries = dto.ShareDeliveries;
        entity.AllowTargetWrite = dto.AllowTargetWrite;
        entity.Note = dto.Note?.Trim() ?? string.Empty;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static UnitDto ToDto(this Unit entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            Name = entity.Name, IsActive = entity.IsActive
        };

    public static void Apply(this Unit entity, UnitDto dto)
    {
        entity.Name = dto.Name; entity.IsActive = dto.IsActive; entity.IsDeleted = dto.IsDeleted;
    }

    public static CustomerCategoryDto ToDto(this CustomerCategory entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            Name = entity.Name, IsSystemDefault = entity.IsSystemDefault
        };

    public static void Apply(this CustomerCategory entity, CustomerCategoryDto dto)
    {
        entity.Name = dto.Name; entity.IsSystemDefault = dto.IsSystemDefault; entity.IsDeleted = dto.IsDeleted;
    }

    public static PriceGradeOptionDto ToDto(this PriceGradeOption entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            Name = entity.Name,
            PriceSource = entity.PriceSource,
            SortOrder = entity.SortOrder,
            IsSystemDefault = entity.IsSystemDefault,
            IsActive = entity.IsActive
        };

    public static void Apply(this PriceGradeOption entity, PriceGradeOptionDto dto)
    {
        entity.Name = dto.Name?.Trim() ?? string.Empty;
        entity.PriceSource = string.IsNullOrWhiteSpace(dto.PriceSource) ? "Sales" : dto.PriceSource.Trim();
        entity.SortOrder = dto.SortOrder;
        entity.IsSystemDefault = dto.IsSystemDefault;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static TradeTypeOptionDto ToDto(this TradeTypeOption entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            Name = entity.Name,
            AllowsSales = entity.AllowsSales,
            AllowsPurchase = entity.AllowsPurchase,
            SortOrder = entity.SortOrder,
            IsSystemDefault = entity.IsSystemDefault,
            IsActive = entity.IsActive
        };

    public static void Apply(this TradeTypeOption entity, TradeTypeOptionDto dto)
    {
        entity.Name = dto.Name?.Trim() ?? string.Empty;
        entity.AllowsSales = dto.AllowsSales;
        entity.AllowsPurchase = dto.AllowsPurchase;
        entity.SortOrder = dto.SortOrder;
        entity.IsSystemDefault = dto.IsSystemDefault;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static ItemCategoryOptionDto ToDto(this ItemCategoryOption entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            Name = entity.Name,
            SortOrder = entity.SortOrder,
            IsSystemDefault = entity.IsSystemDefault,
            IsActive = entity.IsActive
        };

    public static void Apply(this ItemCategoryOption entity, ItemCategoryOptionDto dto)
    {
        entity.Name = dto.Name?.Trim() ?? string.Empty;
        entity.SortOrder = dto.SortOrder;
        entity.IsSystemDefault = dto.IsSystemDefault;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static CustomerMasterDto ToDto(this CustomerMaster entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            NameOriginal = entity.NameOriginal, NameMatchKey = entity.NameMatchKey, CategoryId = entity.CategoryId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode)
        };

    public static void Apply(this CustomerMaster entity, CustomerMasterDto dto)
    {
        entity.NameOriginal = dto.NameOriginal;
        entity.NameMatchKey = string.IsNullOrWhiteSpace(dto.NameMatchKey) ? MatchKeyNormalizer.Normalize(dto.NameOriginal) : dto.NameMatchKey;
        entity.CategoryId = dto.CategoryId; entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
        else if (string.IsNullOrWhiteSpace(entity.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.Shared;
    }

    public static CustomerDto ToDto(this Customer entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            CustomerMasterId = entity.CustomerMasterId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            NameOriginal = entity.NameOriginal,
            NameMatchKey = entity.NameMatchKey, CategoryId = entity.CategoryId,
            TradeType = entity.TradeType,
            Department = entity.Department, ContactPerson = entity.ContactPerson,
            BusinessNumber = entity.BusinessNumber, Address = entity.Address,
            Phone = entity.Phone, Email = entity.Email, Notes = entity.Notes
        };

    public static void Apply(this Customer entity, CustomerDto dto)
    {
        entity.CustomerMasterId = dto.CustomerMasterId; entity.NameOriginal = dto.NameOriginal;
        entity.NameMatchKey = string.IsNullOrWhiteSpace(dto.NameMatchKey) ? MatchKeyNormalizer.Normalize(dto.NameOriginal) : dto.NameMatchKey;
        entity.CategoryId = dto.CategoryId; entity.TradeType = dto.TradeType;
        entity.Department = dto.Department;
        entity.ContactPerson = dto.ContactPerson; entity.BusinessNumber = dto.BusinessNumber;
        entity.Address = dto.Address; entity.Phone = dto.Phone; entity.Email = dto.Email;
        entity.Notes = dto.Notes; entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
        else if (string.IsNullOrWhiteSpace(entity.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.Shared;
    }

    public static CustomerContractDto ToDto(this CustomerContract entity, bool includeContent = false) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            CustomerId = entity.CustomerId,
            ContractType = entity.ContractType,
            FileName = entity.FileName,
            MimeType = entity.MimeType,
            FileSize = entity.FileSize,
            FileHash = entity.FileHash,
            Description = entity.Description,
            SignedDate = entity.SignedDate,
            ExpireDate = entity.ExpireDate,
            IsPrimary = entity.IsPrimary,
            UploadedByUsername = entity.UploadedByUsername,
            UploadedAtUtc = entity.UploadedAtUtc,
            FileContent = includeContent ? entity.FileContent ?? [] : []
        };

    public static void Apply(this CustomerContract entity, CustomerContractDto dto)
    {
        entity.CustomerId = dto.CustomerId;
        entity.ContractType = string.IsNullOrWhiteSpace(dto.ContractType) ? "거래계약서" : dto.ContractType.Trim();
        entity.FileName = dto.FileName?.Trim() ?? string.Empty;
        entity.MimeType = string.IsNullOrWhiteSpace(dto.MimeType) ? "application/pdf" : dto.MimeType.Trim();
        entity.FileSize = dto.FileSize;
        entity.FileHash = dto.FileHash?.Trim() ?? string.Empty;
        entity.Description = dto.Description?.Trim() ?? string.Empty;
        entity.SignedDate = dto.SignedDate;
        entity.ExpireDate = dto.ExpireDate;
        entity.IsPrimary = dto.IsPrimary;
        entity.UploadedByUsername = dto.UploadedByUsername?.Trim() ?? string.Empty;
        entity.UploadedAtUtc = dto.UploadedAtUtc;
        entity.FileContent = dto.FileContent ?? [];
        entity.IsDeleted = dto.IsDeleted;
    }

    public static ItemDto ToDto(this Item entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            NameOriginal = entity.NameOriginal, NameMatchKey = entity.NameMatchKey,
            SpecificationOriginal = entity.SpecificationOriginal, SpecificationMatchKey = entity.SpecificationMatchKey,
            CategoryName = entity.CategoryName,
            Unit = entity.Unit,
            CurrentStock = entity.CurrentStock,
            SafetyStock = entity.SafetyStock,
            PurchasePrice = entity.PurchasePrice,
            SalePrice = entity.SalePrice,
            RetailPrice = entity.RetailPrice,
            PriceGradeA = entity.PriceGradeA,
            PriceGradeB = entity.PriceGradeB,
            PriceGradeC = entity.PriceGradeC,
            SimpleMemo = entity.SimpleMemo,
            IsRental = entity.IsRental, IsSale = entity.IsSale,
            SerialNumber = entity.SerialNumber, MaterialNumber = entity.MaterialNumber,
            InstallLocation = entity.InstallLocation, RentalStartDate = entity.RentalStartDate,
            RentalEndDate = entity.RentalEndDate, Notes = entity.Notes
        };

    public static void Apply(this Item entity, ItemDto dto)
    {
        entity.NameOriginal = dto.NameOriginal;
        entity.NameMatchKey = string.IsNullOrWhiteSpace(dto.NameMatchKey) ? MatchKeyNormalizer.Normalize(dto.NameOriginal) : dto.NameMatchKey;
        entity.SpecificationOriginal = dto.SpecificationOriginal;
        entity.SpecificationMatchKey = string.IsNullOrWhiteSpace(dto.SpecificationMatchKey) ? MatchKeyNormalizer.Normalize(dto.SpecificationOriginal) : dto.SpecificationMatchKey;
        entity.CategoryName = dto.CategoryName;
        entity.Unit = dto.Unit;
        entity.CurrentStock = dto.CurrentStock;
        entity.SafetyStock = dto.SafetyStock;
        entity.PurchasePrice = dto.PurchasePrice;
        entity.SalePrice = dto.SalePrice;
        entity.RetailPrice = dto.RetailPrice;
        entity.PriceGradeA = dto.PriceGradeA;
        entity.PriceGradeB = dto.PriceGradeB;
        entity.PriceGradeC = dto.PriceGradeC;
        entity.SimpleMemo = dto.SimpleMemo;
        entity.IsRental = dto.IsRental; entity.IsSale = dto.IsSale;
        entity.SerialNumber = dto.SerialNumber; entity.MaterialNumber = dto.MaterialNumber;
        entity.InstallLocation = dto.InstallLocation; entity.RentalStartDate = dto.RentalStartDate;
        entity.RentalEndDate = dto.RentalEndDate; entity.Notes = dto.Notes; entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
        else if (string.IsNullOrWhiteSpace(entity.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.Shared;
    }

    public static TransactionDto ToDto(this TransactionRecord entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            CustomerId = entity.CustomerId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            TransactionDate = entity.TransactionDate,
            TransactionKind = entity.TransactionKind,
            LinkedInvoiceId = entity.LinkedInvoiceId,
            LinkedInvoiceNumber = entity.LinkedInvoiceNumber,
            LinkedRentalBillingProfileId = entity.LinkedRentalBillingProfileId,
            SettlementAmount = entity.SettlementAmount,
            AdvanceDelta = entity.AdvanceDelta,
            CashReceipt = entity.CashReceipt,
            CardReceipt = entity.CardReceipt,
            BankReceipt = entity.BankReceipt,
            DiscountApplied = entity.DiscountApplied,
            ReceiptTotal = entity.ReceiptTotal,
            CashPayment = entity.CashPayment,
            CardPayment = entity.CardPayment,
            BankPayment = entity.BankPayment,
            DiscountReceived = entity.DiscountReceived,
            PaymentTotal = entity.PaymentTotal,
            Note = entity.Note,
            Memo = entity.Memo
        };

    public static void Apply(this TransactionRecord entity, TransactionDto dto)
    {
        entity.CustomerId = dto.CustomerId;
        entity.TransactionDate = dto.TransactionDate;
        entity.TransactionKind = dto.TransactionKind?.Trim() ?? string.Empty;
        entity.LinkedInvoiceId = dto.LinkedInvoiceId;
        entity.LinkedInvoiceNumber = dto.LinkedInvoiceNumber?.Trim() ?? string.Empty;
        entity.LinkedRentalBillingProfileId = dto.LinkedRentalBillingProfileId;
        entity.SettlementAmount = dto.SettlementAmount;
        entity.AdvanceDelta = dto.AdvanceDelta;
        entity.CashReceipt = dto.CashReceipt;
        entity.CardReceipt = dto.CardReceipt;
        entity.BankReceipt = dto.BankReceipt;
        entity.DiscountApplied = dto.DiscountApplied;
        entity.ReceiptTotal = dto.ReceiptTotal;
        entity.CashPayment = dto.CashPayment;
        entity.CardPayment = dto.CardPayment;
        entity.BankPayment = dto.BankPayment;
        entity.DiscountReceived = dto.DiscountReceived;
        entity.PaymentTotal = dto.PaymentTotal;
        entity.Note = dto.Note?.Trim() ?? string.Empty;
        entity.Memo = dto.Memo?.Trim() ?? string.Empty;
        entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
            dto.OfficeCode,
            entity.OfficeCode);
    }

    public static TransactionAttachmentDto ToDto(this TransactionAttachment entity, bool includeContent = true) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TransactionId = entity.TransactionId,
            AttachmentType = entity.AttachmentType,
            FileName = entity.FileName,
            MimeType = entity.MimeType,
            FileSize = entity.FileSize,
            FileHash = entity.FileHash,
            Description = entity.Description,
            UploadedByUsername = entity.UploadedByUsername,
            UploadedAtUtc = entity.UploadedAtUtc,
            VerificationStatus = entity.VerificationStatus,
            VerifiedByUsername = entity.VerifiedByUsername,
            VerifiedAtUtc = entity.VerifiedAtUtc,
            VerificationMemo = entity.VerificationMemo,
            SortOrder = entity.SortOrder,
            FileContent = includeContent ? entity.FileContent ?? [] : []
        };

    public static void Apply(this TransactionAttachment entity, TransactionAttachmentDto dto)
    {
        entity.TransactionId = dto.TransactionId;
        entity.AttachmentType = dto.AttachmentType?.Trim() ?? "기타";
        entity.FileName = dto.FileName?.Trim() ?? string.Empty;
        entity.MimeType = dto.MimeType?.Trim() ?? string.Empty;
        entity.FileSize = dto.FileSize;
        entity.FileHash = dto.FileHash?.Trim() ?? string.Empty;
        entity.Description = dto.Description?.Trim() ?? string.Empty;
        entity.UploadedByUsername = dto.UploadedByUsername?.Trim() ?? string.Empty;
        entity.UploadedAtUtc = dto.UploadedAtUtc;
        entity.VerificationStatus = dto.VerificationStatus?.Trim() ?? "미확인";
        entity.VerifiedByUsername = dto.VerifiedByUsername?.Trim() ?? string.Empty;
        entity.VerifiedAtUtc = dto.VerifiedAtUtc;
        entity.VerificationMemo = dto.VerificationMemo?.Trim() ?? string.Empty;
        entity.SortOrder = dto.SortOrder;
        entity.FileContent = dto.FileContent ?? [];
        entity.IsDeleted = dto.IsDeleted;
    }

    public static InventoryTransferDto ToDto(this InventoryTransfer entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.SourceOfficeCode),
            SourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.SourceOfficeCode),
            TargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.TargetOfficeCode),
            TransferNumber = entity.TransferNumber,
            TransferDate = entity.TransferDate,
            FromWarehouseCode = entity.FromWarehouseCode,
            ToWarehouseCode = entity.ToWarehouseCode,
            Memo = entity.Memo,
            CreatedByUsername = entity.CreatedByUsername,
            LastSavedByUsername = entity.LastSavedByUsername,
            LastSavedAtUtc = entity.LastSavedAtUtc,
            TransferStatus = entity.TransferStatus,
            RequestedByUsername = entity.RequestedByUsername,
            RequestedAtUtc = entity.RequestedAtUtc,
            ReceivedByUsername = entity.ReceivedByUsername,
            ReceivedAtUtc = entity.ReceivedAtUtc,
            ReceiveMemo = entity.ReceiveMemo,
            ReceiveEvidencePath = entity.ReceiveEvidencePath,
            RejectedByUsername = entity.RejectedByUsername,
            RejectedAtUtc = entity.RejectedAtUtc,
            RejectReason = entity.RejectReason,
            LastStatusChangedByUsername = entity.LastStatusChangedByUsername,
            LastStatusChangedAtUtc = entity.LastStatusChangedAtUtc,
            Lines = entity.Lines
                .Where(line => !line.IsDeleted)
                .OrderBy(line => line.Id)
                .Select(line => line.ToDto())
                .ToList()
        };

    public static InventoryTransferLineDto ToDto(this InventoryTransferLine entity) =>
        new()
        {
            Id = entity.Id,
            TransferId = entity.TransferId,
            ItemId = entity.ItemId,
            ItemNameOriginal = entity.ItemNameOriginal,
            SpecificationOriginal = entity.SpecificationOriginal,
            Unit = entity.Unit,
            Quantity = entity.Quantity,
            ReceivedQuantity = entity.ReceivedQuantity,
            QuantityDifference = entity.QuantityDifference,
            Remark = entity.Remark,
            ReceiptRemark = entity.ReceiptRemark,
            IsDeleted = entity.IsDeleted
        };

    public static void Apply(this InventoryTransfer entity, InventoryTransferDto dto)
    {
        entity.TransferNumber = dto.TransferNumber?.Trim() ?? string.Empty;
        entity.TransferDate = dto.TransferDate;
        entity.FromWarehouseCode = dto.FromWarehouseCode?.Trim() ?? string.Empty;
        entity.ToWarehouseCode = dto.ToWarehouseCode?.Trim() ?? string.Empty;
        entity.Memo = dto.Memo?.Trim() ?? string.Empty;
        entity.CreatedByUsername = dto.CreatedByUsername?.Trim() ?? string.Empty;
        entity.LastSavedByUsername = dto.LastSavedByUsername?.Trim() ?? string.Empty;
        entity.LastSavedAtUtc = dto.LastSavedAtUtc;
        entity.TransferStatus = dto.TransferStatus?.Trim() ?? "수령대기";
        entity.RequestedByUsername = dto.RequestedByUsername?.Trim() ?? string.Empty;
        entity.RequestedAtUtc = dto.RequestedAtUtc;
        entity.ReceivedByUsername = dto.ReceivedByUsername?.Trim() ?? string.Empty;
        entity.ReceivedAtUtc = dto.ReceivedAtUtc;
        entity.ReceiveMemo = dto.ReceiveMemo?.Trim() ?? string.Empty;
        entity.ReceiveEvidencePath = dto.ReceiveEvidencePath?.Trim() ?? string.Empty;
        entity.RejectedByUsername = dto.RejectedByUsername?.Trim() ?? string.Empty;
        entity.RejectedAtUtc = dto.RejectedAtUtc;
        entity.RejectReason = dto.RejectReason?.Trim() ?? string.Empty;
        entity.LastStatusChangedByUsername = dto.LastStatusChangedByUsername?.Trim() ?? string.Empty;
        entity.LastStatusChangedAtUtc = dto.LastStatusChangedAtUtc;
        entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.SourceOfficeCode,
            entity.TenantCode,
            entity.SourceOfficeCode);
        entity.SourceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.SourceOfficeCode, entity.SourceOfficeCode);
        entity.TargetOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(dto.TargetOfficeCode, entity.TargetOfficeCode);
    }

    public static InventoryTransferLine ToEntity(this InventoryTransferLineDto dto, Guid transferId) =>
        new()
        {
            Id = dto.Id == Guid.Empty ? Guid.NewGuid() : dto.Id,
            TransferId = transferId,
            ItemId = dto.ItemId,
            ItemNameOriginal = dto.ItemNameOriginal?.Trim() ?? string.Empty,
            SpecificationOriginal = dto.SpecificationOriginal?.Trim() ?? string.Empty,
            Unit = dto.Unit?.Trim() ?? string.Empty,
            Quantity = dto.Quantity,
            ReceivedQuantity = dto.ReceivedQuantity,
            QuantityDifference = dto.QuantityDifference,
            Remark = dto.Remark?.Trim() ?? string.Empty,
            ReceiptRemark = dto.ReceiptRemark?.Trim() ?? string.Empty,
            IsDeleted = dto.IsDeleted
        };

    public static RentalManagementCompanyDto ToDto(this RentalManagementCompany entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(entity.TenantCode),
            Code = entity.Code,
            Name = entity.Name,
            IsSystemDefault = entity.IsSystemDefault,
            IsActive = entity.IsActive
        };

    public static void Apply(this RentalManagementCompany entity, RentalManagementCompanyDto dto)
    {
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeOrDefault(dto.TenantCode, entity.TenantCode);
        entity.Code = dto.Code?.Trim() ?? string.Empty;
        entity.Name = dto.Name?.Trim() ?? string.Empty;
        entity.IsSystemDefault = dto.IsSystemDefault;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
    }

    public static RentalBillingProfileDto ToDto(this RentalBillingProfile entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            ProfileKey = entity.ProfileKey,
            CustomerId = entity.CustomerId,
            CustomerName = entity.CustomerName,
            BusinessNumber = entity.BusinessNumber,
            RealCustomerName = entity.RealCustomerName,
            ModelName = entity.ModelName,
            ManagementCompanyCode = entity.ManagementCompanyCode,
            BillingMethod = entity.BillingMethod,
            PaymentMethod = entity.PaymentMethod,
            BillingStatus = entity.BillingStatus,
            Email = entity.Email,
            BillingDay = entity.BillingDay,
            BillingCycleMonths = entity.BillingCycleMonths,
            MonthlyAmount = entity.MonthlyAmount,
            DepositAmount = entity.DepositAmount,
            SubmissionDocuments = entity.SubmissionDocuments,
            Notes = entity.Notes,
            BillingAnchorDate = entity.BillingAnchorDate,
            ContractDate = entity.ContractDate,
            ContractStartDate = entity.ContractStartDate,
            ContractEndDate = entity.ContractEndDate,
            LastBilledDate = entity.LastBilledDate,
            SettlementStatus = entity.SettlementStatus,
            CompletionStatus = entity.CompletionStatus,
            SettledAmount = entity.SettledAmount,
            OutstandingAmount = entity.OutstandingAmount,
            RequiresFollowUp = entity.RequiresFollowUp,
            FollowUpNote = entity.FollowUpNote,
            LastSettledDate = entity.LastSettledDate,
            AssignedUsername = entity.AssignedUsername,
            IsActive = entity.IsActive
        };

    public static void Apply(this RentalBillingProfile entity, RentalBillingProfileDto dto)
    {
        entity.ProfileKey = dto.ProfileKey?.Trim() ?? string.Empty;
        entity.CustomerId = dto.CustomerId;
        entity.CustomerName = dto.CustomerName?.Trim() ?? string.Empty;
        entity.BusinessNumber = dto.BusinessNumber?.Trim() ?? string.Empty;
        entity.RealCustomerName = dto.RealCustomerName?.Trim() ?? string.Empty;
        entity.ModelName = dto.ModelName?.Trim() ?? string.Empty;
        entity.ManagementCompanyCode = dto.ManagementCompanyCode?.Trim() ?? string.Empty;
        entity.BillingMethod = dto.BillingMethod?.Trim() ?? string.Empty;
        entity.PaymentMethod = dto.PaymentMethod?.Trim() ?? string.Empty;
        entity.BillingStatus = dto.BillingStatus?.Trim() ?? string.Empty;
        entity.Email = dto.Email?.Trim() ?? string.Empty;
        entity.BillingDay = dto.BillingDay;
        entity.BillingCycleMonths = dto.BillingCycleMonths;
        entity.MonthlyAmount = dto.MonthlyAmount;
        entity.DepositAmount = dto.DepositAmount;
        entity.SubmissionDocuments = dto.SubmissionDocuments?.Trim() ?? string.Empty;
        entity.Notes = dto.Notes?.Trim() ?? string.Empty;
        entity.BillingAnchorDate = dto.BillingAnchorDate;
        entity.ContractDate = dto.ContractDate;
        entity.ContractStartDate = dto.ContractStartDate;
        entity.ContractEndDate = dto.ContractEndDate;
        entity.LastBilledDate = dto.LastBilledDate;
        entity.SettlementStatus = dto.SettlementStatus?.Trim() ?? string.Empty;
        entity.CompletionStatus = dto.CompletionStatus?.Trim() ?? string.Empty;
        entity.SettledAmount = dto.SettledAmount;
        entity.OutstandingAmount = dto.OutstandingAmount;
        entity.RequiresFollowUp = dto.RequiresFollowUp;
        entity.FollowUpNote = dto.FollowUpNote?.Trim() ?? string.Empty;
        entity.LastSettledDate = dto.LastSettledDate;
        entity.AssignedUsername = dto.AssignedUsername?.Trim() ?? string.Empty;
        entity.IsActive = dto.IsActive;
        entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
    }

    public static RentalAssetDto ToDto(this RentalAsset entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            AssetKey = entity.AssetKey,
            CustomerId = entity.CustomerId,
            ItemId = entity.ItemId,
            BillingProfileId = entity.BillingProfileId,
            ManagementId = entity.ManagementId,
            ManagementNumber = entity.ManagementNumber,
            ManagementCompanyCode = entity.ManagementCompanyCode,
            CurrentLocation = entity.CurrentLocation,
            ProductCategory = entity.ProductCategory,
            Manufacturer = entity.Manufacturer,
            ModelName = entity.ModelName,
            MachineNumber = entity.MachineNumber,
            PurchaseVendor = entity.PurchaseVendor,
            PurchaseDate = entity.PurchaseDate,
            DisposalDate = entity.DisposalDate,
            PurchasePrice = entity.PurchasePrice,
            SalePrice = entity.SalePrice,
            CustomerName = entity.CustomerName,
            InstallLocation = entity.InstallLocation,
            DepositText = entity.DepositText,
            MonthlyFee = entity.MonthlyFee,
            ContractMonths = entity.ContractMonths,
            ContractDate = entity.ContractDate,
            InstallDate = entity.InstallDate,
            ContractStartDate = entity.ContractStartDate,
            RentalEndDate = entity.RentalEndDate,
            FreeSupplyItems = entity.FreeSupplyItems,
            PaidSupplyItems = entity.PaidSupplyItems,
            AssignedUsername = entity.AssignedUsername,
            AssetStatus = entity.AssetStatus,
            Notes = entity.Notes
        };

    public static void Apply(this RentalAsset entity, RentalAssetDto dto)
    {
        entity.AssetKey = dto.AssetKey?.Trim() ?? string.Empty;
        entity.CustomerId = dto.CustomerId;
        entity.ItemId = dto.ItemId;
        entity.BillingProfileId = dto.BillingProfileId;
        entity.ManagementId = dto.ManagementId?.Trim() ?? string.Empty;
        entity.ManagementNumber = dto.ManagementNumber?.Trim() ?? string.Empty;
        entity.ManagementCompanyCode = dto.ManagementCompanyCode?.Trim() ?? string.Empty;
        entity.CurrentLocation = dto.CurrentLocation?.Trim() ?? string.Empty;
        entity.ProductCategory = dto.ProductCategory?.Trim() ?? string.Empty;
        entity.Manufacturer = dto.Manufacturer?.Trim() ?? string.Empty;
        entity.ModelName = dto.ModelName?.Trim() ?? string.Empty;
        entity.MachineNumber = dto.MachineNumber?.Trim() ?? string.Empty;
        entity.PurchaseVendor = dto.PurchaseVendor?.Trim() ?? string.Empty;
        entity.PurchaseDate = dto.PurchaseDate;
        entity.DisposalDate = dto.DisposalDate;
        entity.PurchasePrice = dto.PurchasePrice;
        entity.SalePrice = dto.SalePrice;
        entity.CustomerName = dto.CustomerName?.Trim() ?? string.Empty;
        entity.InstallLocation = dto.InstallLocation?.Trim() ?? string.Empty;
        entity.DepositText = dto.DepositText?.Trim() ?? string.Empty;
        entity.MonthlyFee = dto.MonthlyFee;
        entity.ContractMonths = dto.ContractMonths;
        entity.ContractDate = dto.ContractDate;
        entity.InstallDate = dto.InstallDate;
        entity.ContractStartDate = dto.ContractStartDate;
        entity.RentalEndDate = dto.RentalEndDate;
        entity.FreeSupplyItems = dto.FreeSupplyItems?.Trim() ?? string.Empty;
        entity.PaidSupplyItems = dto.PaidSupplyItems?.Trim() ?? string.Empty;
        entity.AssignedUsername = dto.AssignedUsername?.Trim() ?? string.Empty;
        entity.AssetStatus = dto.AssetStatus?.Trim() ?? string.Empty;
        entity.Notes = dto.Notes?.Trim() ?? string.Empty;
        entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
    }

    public static RentalBillingLogDto ToDto(this RentalBillingLog entity) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            BillingProfileId = entity.BillingProfileId,
            BillingYearMonth = entity.BillingYearMonth,
            ScheduledDate = entity.ScheduledDate,
            ProcessedDate = entity.ProcessedDate,
            ProcessedByUsername = entity.ProcessedByUsername,
            Status = entity.Status,
            BilledAmount = entity.BilledAmount,
            Note = entity.Note,
            AssignedUsername = entity.AssignedUsername
        };

    public static void Apply(this RentalBillingLog entity, RentalBillingLogDto dto)
    {
        entity.BillingProfileId = dto.BillingProfileId;
        entity.BillingYearMonth = dto.BillingYearMonth?.Trim() ?? string.Empty;
        entity.ScheduledDate = dto.ScheduledDate;
        entity.ProcessedDate = dto.ProcessedDate;
        entity.ProcessedByUsername = dto.ProcessedByUsername?.Trim() ?? string.Empty;
        entity.Status = dto.Status?.Trim() ?? "예정";
        entity.BilledAmount = dto.BilledAmount;
        entity.Note = dto.Note?.Trim() ?? string.Empty;
        entity.AssignedUsername = dto.AssignedUsername?.Trim() ?? string.Empty;
        entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
    }

    public static InvoiceDto ToDto(this Invoice entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            CustomerId = entity.CustomerId,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(entity.TenantCode, entity.OfficeCode),
            OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(entity.OfficeCode),
            InvoiceNumber = entity.InvoiceNumber,
            LocalTempNumber = entity.LocalTempNumber, VoucherType = entity.VoucherType,
            InvoiceDate = entity.InvoiceDate, TotalAmount = entity.TotalAmount,
            SupplyAmount = entity.SupplyAmount, VatAmount = entity.VatAmount, Memo = entity.Memo,
            Lines = entity.Lines.Where(x => !x.IsDeleted).OrderBy(x => x.Id).Select(x => x.ToDto()).ToList(),
            Payments = entity.Payments.Where(x => !x.IsDeleted).OrderByDescending(x => x.PaymentDate).Select(x => x.ToDto()).ToList()
        };

    public static InvoiceLineDto ToDto(this InvoiceLine entity) =>
        new()
        {
            Id = entity.Id, InvoiceId = entity.InvoiceId, ItemId = entity.ItemId,
            ItemNameOriginal = entity.ItemNameOriginal, SpecificationOriginal = entity.SpecificationOriginal,
            Unit = entity.Unit, Quantity = entity.Quantity, UnitPrice = entity.UnitPrice,
            LineAmount = entity.LineAmount, Remark = entity.Remark,
            SerialNumber = entity.SerialNumber, MaterialNumber = entity.MaterialNumber,
            InstallLocation = entity.InstallLocation, RentalStartDate = entity.RentalStartDate,
            RentalEndDate = entity.RentalEndDate, IsDeleted = entity.IsDeleted
        };

    public static void Apply(this Invoice entity, InvoiceDto dto)
    {
        entity.CustomerId = dto.CustomerId; entity.InvoiceNumber = dto.InvoiceNumber;
        entity.LocalTempNumber = dto.LocalTempNumber; entity.VoucherType = dto.VoucherType;
        entity.InvoiceDate = dto.InvoiceDate; entity.Memo = dto.Memo; entity.IsDeleted = dto.IsDeleted;
        entity.TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dto.OfficeCode,
            entity.TenantCode,
            entity.OfficeCode);
        if (!string.IsNullOrWhiteSpace(dto.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(dto.OfficeCode, entity.OfficeCode);
        else if (string.IsNullOrWhiteSpace(entity.OfficeCode))
            entity.OfficeCode = OfficeCodeCatalog.Shared;
        var lines = dto.Lines ?? [];
        entity.TotalAmount = lines.Sum(x => x.LineAmount);
        entity.SupplyAmount = Math.Round(entity.TotalAmount / 1.1m, 0, MidpointRounding.AwayFromZero);
        entity.VatAmount = entity.TotalAmount - entity.SupplyAmount;
    }

    public static PaymentDto ToDto(this Payment entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            InvoiceId = entity.InvoiceId, PaymentDate = entity.PaymentDate,
            Amount = entity.Amount, Note = entity.Note,
            Attachments = entity.Attachments
                .Where(x => !x.IsDeleted)
                .OrderByDescending(x => x.UploadedAtUtc)
                .Select(x => x.ToDto(false))
                .ToList()
        };

    public static void Apply(this Payment entity, PaymentDto dto)
    {
        entity.InvoiceId = dto.InvoiceId; entity.PaymentDate = dto.PaymentDate;
        entity.Amount = dto.Amount; entity.Note = dto.Note; entity.IsDeleted = dto.IsDeleted;
    }

    public static PaymentAttachmentDto ToDto(this PaymentAttachment entity, bool includeContent = false) =>
        new()
        {
            Id = entity.Id,
            IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc,
            UpdatedAtUtc = entity.UpdatedAtUtc,
            Revision = entity.Revision,
            PaymentId = entity.PaymentId,
            AttachmentType = entity.AttachmentType,
            FileName = entity.FileName,
            MimeType = entity.MimeType,
            FileSize = entity.FileSize,
            FileHash = entity.FileHash,
            Description = entity.Description,
            UploadedAtUtc = entity.UploadedAtUtc,
            FileContent = includeContent ? entity.FileContent ?? [] : []
        };

    public static void Apply(this PaymentAttachment entity, PaymentAttachmentDto dto)
    {
        entity.PaymentId = dto.PaymentId;
        entity.AttachmentType = dto.AttachmentType?.Trim() ?? "내역첨부";
        entity.FileName = dto.FileName?.Trim() ?? string.Empty;
        entity.MimeType = dto.MimeType?.Trim() ?? string.Empty;
        entity.FileSize = dto.FileSize;
        entity.FileHash = dto.FileHash?.Trim() ?? string.Empty;
        entity.Description = dto.Description?.Trim() ?? string.Empty;
        entity.UploadedAtUtc = dto.UploadedAtUtc;
        entity.FileContent = dto.FileContent ?? [];
        entity.IsDeleted = dto.IsDeleted;
    }

    public static ItemWarehouseStockDto ToDto(this ItemWarehouseStock entity) =>
        new()
        {
            ItemId = entity.ItemId,
            WarehouseCode = entity.WarehouseCode,
            Quantity = entity.Quantity,
            UpdatedAtUtc = entity.UpdatedAtUtc
        };

    public static AuditLogDto ToDto(this AuditLog entity) =>
        new()
        {
            Id = entity.Id, UserId = entity.UserId, Username = entity.Username,
            EntityName = entity.EntityName, EntityId = entity.EntityId, Action = entity.Action,
            BeforeJson = entity.BeforeJson, AfterJson = entity.AfterJson, CreatedAtUtc = entity.CreatedAtUtc
        };

    public static ConflictLogDto ToDto(this ConflictLog entity) =>
        new()
        {
            Id = entity.Id, UserId = entity.UserId, Username = entity.Username,
            EntityName = entity.EntityName, EntityId = entity.EntityId,
            ClientJson = entity.ClientJson, ServerJson = entity.ServerJson,
            Reason = entity.Reason, CreatedAtUtc = entity.CreatedAtUtc
        };
}
