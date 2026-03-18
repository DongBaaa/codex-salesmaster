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
            Username = entity.Username, Role = entity.Role, OfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(entity.OfficeCode), IsActive = entity.IsActive,
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

    public static void Apply(this CompanyProfile entity, CompanyProfileDto dto)
    {
        entity.TradeName = dto.TradeName; entity.Representative = dto.Representative;
        entity.BusinessNumber = dto.BusinessNumber; entity.BusinessType = dto.BusinessType;
        entity.BusinessItem = dto.BusinessItem; entity.Address = dto.Address;
        entity.ContactNumber = dto.ContactNumber; entity.Email = dto.Email;
        entity.BankAccountText = dto.BankAccountText; entity.StampImage = dto.StampImage;
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
            NameOriginal = entity.NameOriginal, NameMatchKey = entity.NameMatchKey, CategoryId = entity.CategoryId
        };

    public static void Apply(this CustomerMaster entity, CustomerMasterDto dto)
    {
        entity.NameOriginal = dto.NameOriginal;
        entity.NameMatchKey = string.IsNullOrWhiteSpace(dto.NameMatchKey) ? MatchKeyNormalizer.Normalize(dto.NameOriginal) : dto.NameMatchKey;
        entity.CategoryId = dto.CategoryId; entity.IsDeleted = dto.IsDeleted;
    }

    public static CustomerDto ToDto(this Customer entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            CustomerMasterId = entity.CustomerMasterId, NameOriginal = entity.NameOriginal,
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
    }

    public static ItemDto ToDto(this Item entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            NameOriginal = entity.NameOriginal, NameMatchKey = entity.NameMatchKey,
            SpecificationOriginal = entity.SpecificationOriginal, SpecificationMatchKey = entity.SpecificationMatchKey,
            Unit = entity.Unit, IsRental = entity.IsRental, IsSale = entity.IsSale,
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
        entity.Unit = dto.Unit; entity.IsRental = dto.IsRental; entity.IsSale = dto.IsSale;
        entity.SerialNumber = dto.SerialNumber; entity.MaterialNumber = dto.MaterialNumber;
        entity.InstallLocation = dto.InstallLocation; entity.RentalStartDate = dto.RentalStartDate;
        entity.RentalEndDate = dto.RentalEndDate; entity.Notes = dto.Notes; entity.IsDeleted = dto.IsDeleted;
    }

    public static InvoiceDto ToDto(this Invoice entity) =>
        new()
        {
            Id = entity.Id, IsDeleted = entity.IsDeleted,
            CreatedAtUtc = entity.CreatedAtUtc, UpdatedAtUtc = entity.UpdatedAtUtc, Revision = entity.Revision,
            CustomerId = entity.CustomerId, InvoiceNumber = entity.InvoiceNumber,
            LocalTempNumber = entity.LocalTempNumber, VoucherType = entity.VoucherType,
            InvoiceDate = entity.InvoiceDate, TotalAmount = entity.TotalAmount,
            SupplyAmount = entity.SupplyAmount, VatAmount = entity.VatAmount, Memo = entity.Memo,
            Lines = entity.Lines.Where(x => !x.IsDeleted).OrderBy(x => x.Id).Select(x => x.ToDto()).ToList()
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
            Amount = entity.Amount, Note = entity.Note
        };

    public static void Apply(this Payment entity, PaymentDto dto)
    {
        entity.InvoiceId = dto.InvoiceId; entity.PaymentDate = dto.PaymentDate;
        entity.Amount = dto.Amount; entity.Note = dto.Note; entity.IsDeleted = dto.IsDeleted;
    }

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
