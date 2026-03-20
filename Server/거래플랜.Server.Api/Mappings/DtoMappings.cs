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
