using 거래플랜.Server.Api.Data;
using 거래플랜.Server.Api.Domain;
using 거래플랜.Shared.Contracts;
using Microsoft.EntityFrameworkCore;

namespace 거래플랜.Server.Api.Services;

internal static class InvoiceStructuralMutationGuard
{
    public static async Task<bool> ShouldProtectExistingInvoiceFromSameIdStructuralMutationAsync(
        AppDbContext dbContext,
        Invoice existing,
        InvoiceDto dto,
        CancellationToken cancellationToken,
        bool protectRentalLinks = true,
        bool allowSameRentalTargetTransactions = false)
    {
        if (protectRentalLinks &&
            (HasGuid(existing.LinkedRentalBillingProfileId) ||
             HasGuid(existing.LinkedRentalBillingRunId) ||
             HasGuid(dto.LinkedRentalBillingProfileId) ||
             HasGuid(dto.LinkedRentalBillingRunId)))
        {
            return true;
        }

        if (!existing.IsLatestVersion ||
            existing.VersionNumber > 1 ||
            HasGuid(existing.PreviousVersionId))
        {
            return true;
        }

        var versionGroupId = existing.VersionGroupId == Guid.Empty ? existing.Id : existing.VersionGroupId;
        if (versionGroupId != Guid.Empty &&
            await dbContext.Invoices.IgnoreQueryFilters()
                .AsNoTracking()
                .AnyAsync(invoice =>
                        invoice.Id != existing.Id &&
                        (invoice.VersionGroupId == versionGroupId ||
                         (invoice.VersionGroupId == Guid.Empty && invoice.Id == versionGroupId)),
                    cancellationToken))
        {
            return true;
        }

        if (await HasActivePaymentRecordsAsync(dbContext, [existing.Id], cancellationToken))
            return true;

        return await HasActiveTransactionSideEffectsAsync(
            dbContext,
            [existing.Id],
            allowSameRentalTargetTransactions ? existing : null,
            cancellationToken);
    }

    public static async Task<bool> HasActivePaymentSideEffectsAsync(
        AppDbContext dbContext,
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
        => await HasActivePaymentRecordsAsync(dbContext, invoiceIds, cancellationToken) ||
           await HasActiveTransactionSideEffectsAsync(dbContext, invoiceIds, allowedSameRentalTargetInvoice: null, cancellationToken);

    private static async Task<bool> HasActivePaymentRecordsAsync(
        AppDbContext dbContext,
        IReadOnlyCollection<Guid> invoiceIds,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return false;

        return await dbContext.Payments.IgnoreQueryFilters()
            .AnyAsync(payment => !payment.IsDeleted && invoiceIds.Contains(payment.InvoiceId), cancellationToken);
    }

    private static async Task<bool> HasActiveTransactionSideEffectsAsync(
        AppDbContext dbContext,
        IReadOnlyCollection<Guid> invoiceIds,
        Invoice? allowedSameRentalTargetInvoice,
        CancellationToken cancellationToken)
    {
        if (invoiceIds.Count == 0)
            return false;

        var activeTransactions = dbContext.Transactions.IgnoreQueryFilters()
            .Where(transaction =>
                !transaction.IsDeleted &&
                transaction.LinkedInvoiceId.HasValue &&
                invoiceIds.Contains(transaction.LinkedInvoiceId.Value));

        if (allowedSameRentalTargetInvoice is null ||
            !HasGuid(allowedSameRentalTargetInvoice.LinkedRentalBillingProfileId))
        {
            return await activeTransactions.AnyAsync(cancellationToken);
        }

        var allowedProfileId = allowedSameRentalTargetInvoice.LinkedRentalBillingProfileId!.Value;
        var allowedRunId = NormalizeGuardGuid(allowedSameRentalTargetInvoice.LinkedRentalBillingRunId);
        return await dbContext.Transactions.IgnoreQueryFilters()
            .AnyAsync(transaction =>
                    !transaction.IsDeleted &&
                    transaction.LinkedInvoiceId.HasValue &&
                    invoiceIds.Contains(transaction.LinkedInvoiceId.Value) &&
                    (transaction.LinkedRentalBillingProfileId != allowedProfileId ||
                     (allowedRunId.HasValue
                         ? transaction.LinkedRentalBillingRunId != allowedRunId.Value
                         : transaction.LinkedRentalBillingRunId.HasValue && transaction.LinkedRentalBillingRunId.Value != Guid.Empty)),
                cancellationToken);
    }

    public static bool HasSameIdInvoiceStructuralMutation(Invoice existing, InvoiceDto dto)
    {
        if (existing.IsDeleted != dto.IsDeleted)
            return true;
        if (existing.CustomerId != dto.CustomerId)
            return true;
        if (!SameInvoiceScope(existing, dto))
            return true;
        if (existing.VoucherType != dto.VoucherType)
            return true;
        if (!SameText(
                OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                    existing.SourceWarehouseCode,
                    existing.ResponsibleOfficeCode,
                    existing.OfficeCode),
                OfficeCodeCatalog.NormalizeWarehouseCodeOrDefault(
                    dto.SourceWarehouseCode,
                    dto.ResponsibleOfficeCode,
                    dto.OfficeCode)))
        {
            return true;
        }

        if (existing.InvoiceDate != dto.InvoiceDate)
            return true;
        if (!SameText(InvoiceVatModes.Normalize(existing.VatMode), InvoiceVatModes.Normalize(dto.VatMode)))
            return true;
        if (existing.TaxInvoiceIssued != dto.TaxInvoiceIssued)
            return true;
        if (existing.PurchaseReceivingRequired != dto.PurchaseReceivingRequired)
            return true;
        if (!SameText(
                InvoiceReceivingStatuses.Normalize(
                    existing.PurchaseReceivingStatus,
                    existing.VoucherType == VoucherType.Purchase,
                    existing.PurchaseReceivingRequired),
                InvoiceReceivingStatuses.Normalize(
                    dto.PurchaseReceivingStatus,
                    dto.VoucherType == VoucherType.Purchase,
                    dto.PurchaseReceivingRequired)))
        {
            return true;
        }

        if (NormalizeInvoiceGuardUtc(existing.PurchaseReceivedAtUtc) != NormalizeInvoiceGuardUtc(dto.PurchaseReceivedAtUtc))
            return true;
        if (!SameText(existing.PurchaseReceivedByUsername, dto.PurchaseReceivedByUsername))
            return true;
        if (!SameText(existing.PurchaseReceivingOfficeCode, dto.PurchaseReceivingOfficeCode))
            return true;
        if (!SameText(existing.PurchaseReceivingWarehouseCode, dto.PurchaseReceivingWarehouseCode))
            return true;
        if (!SameText(existing.PurchaseReceivingMemo, dto.PurchaseReceivingMemo))
            return true;
        if (NormalizeGuardGuid(existing.LinkedRentalBillingProfileId) != NormalizeGuardGuid(dto.LinkedRentalBillingProfileId))
            return true;
        if (NormalizeGuardGuid(existing.LinkedRentalBillingRunId) != NormalizeGuardGuid(dto.LinkedRentalBillingRunId))
            return true;

        return !HaveSameInvoiceLineStructure(existing.Lines, dto.Lines ?? []);
    }

    private static bool SameInvoiceScope(Invoice existing, InvoiceDto dto)
    {
        var existingResponsibleOffice = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            existing.ResponsibleOfficeCode,
            existing.OfficeCode,
            OfficeCodeCatalog.Usenet);
        var dtoResponsibleOffice = OfficeCodeCatalog.NormalizeOfficeCodeLoose(
            dto.ResponsibleOfficeCode,
            dto.OfficeCode,
            OfficeCodeCatalog.Usenet);
        if (!SameText(existingResponsibleOffice, dtoResponsibleOffice))
            return false;

        var existingOffice = OfficeCodeCatalog.ResolveOwningOfficeCode(
            existing.OfficeCode,
            existingResponsibleOffice,
            existing.OfficeCode);
        var dtoOffice = OfficeCodeCatalog.ResolveOwningOfficeCode(
            dto.OfficeCode,
            dtoResponsibleOffice,
            existingOffice);
        if (!SameText(existingOffice, dtoOffice))
            return false;

        var existingTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            existing.TenantCode,
            existingOffice,
            existing.TenantCode,
            existingResponsibleOffice);
        var dtoTenant = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(
            dto.TenantCode,
            dtoOffice,
            existingTenant,
            dtoResponsibleOffice);
        return SameText(existingTenant, dtoTenant);
    }

    private static bool HaveSameInvoiceLineStructure(
        IEnumerable<InvoiceLine> existingLines,
        IEnumerable<InvoiceLineDto> dtoLines)
    {
        var existingSignatures = existingLines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
            .ThenBy(line => line.Id)
            .Select((line, index) => InvoiceLineGuardSignature.FromExisting(line, index + 1))
            .ToList();
        var dtoSignatures = dtoLines
            .Where(line => !line.IsDeleted)
            .Select((line, index) => InvoiceLineGuardSignature.FromDto(line, index + 1))
            .ToList();

        return existingSignatures.SequenceEqual(dtoSignatures);
    }

    private static Guid? NormalizeGuardGuid(Guid? value)
        => !value.HasValue || value.Value == Guid.Empty ? null : value.Value;

    private static bool HasGuid(Guid? value)
        => value.HasValue && value.Value != Guid.Empty;

    private static DateTime? NormalizeInvoiceGuardUtc(DateTime? value)
    {
        if (!value.HasValue)
            return null;

        var current = value.Value;
        return current.Kind switch
        {
            DateTimeKind.Utc => current,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(current, DateTimeKind.Utc),
            _ => current.ToUniversalTime()
        };
    }

    private static bool SameText(string? left, string? right)
        => string.Equals(left ?? string.Empty, right ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private readonly record struct InvoiceLineGuardSignature(
        int Position,
        Guid Id,
        Guid? ItemId,
        string ItemNameOriginal,
        string SpecificationOriginal,
        string Unit,
        decimal Quantity,
        decimal UnitPrice,
        decimal LineAmount,
        string Remark,
        string SerialNumber,
        string MaterialNumber,
        string InstallLocation,
        DateOnly? RentalStartDate,
        DateOnly? RentalEndDate,
        string ItemTrackingType)
    {
        public static InvoiceLineGuardSignature FromExisting(InvoiceLine line, int position)
            => new(
                position,
                line.Id,
                NormalizeGuardGuid(line.ItemId),
                line.ItemNameOriginal ?? string.Empty,
                line.SpecificationOriginal ?? string.Empty,
                UnitCatalogNormalizer.Normalize(line.Unit),
                line.Quantity,
                line.UnitPrice,
                line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount,
                line.Remark ?? string.Empty,
                line.SerialNumber ?? string.Empty,
                line.MaterialNumber ?? string.Empty,
                line.InstallLocation ?? string.Empty,
                line.RentalStartDate,
                line.RentalEndDate,
                ItemTrackingTypes.Normalize(line.ItemTrackingType));

        public static InvoiceLineGuardSignature FromDto(InvoiceLineDto line, int position)
            => new(
                position,
                line.Id,
                NormalizeGuardGuid(line.ItemId),
                line.ItemNameOriginal ?? string.Empty,
                line.SpecificationOriginal ?? string.Empty,
                UnitCatalogNormalizer.Normalize(line.Unit),
                line.Quantity,
                line.UnitPrice,
                line.LineAmount == 0 ? line.Quantity * line.UnitPrice : line.LineAmount,
                line.Remark ?? string.Empty,
                line.SerialNumber ?? string.Empty,
                line.MaterialNumber ?? string.Empty,
                line.InstallLocation ?? string.Empty,
                line.RentalStartDate,
                line.RentalEndDate,
                ItemTrackingTypes.Normalize(line.ItemTrackingType));
    }
}
