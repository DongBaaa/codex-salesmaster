namespace 거래플랜.Desktop.App.Printing;

public sealed record AttachmentDocumentDefinition(string Code, string DisplayName);

public static class AttachmentDocumentCatalog
{
    public const string Statement = "statement";
    public const string Estimate = "estimate";
    public const string PaymentClaim = "payment_claim";
    public const string NationalTaxCompletion = "national_tax_completion";
    public const string LocalTaxCompletion = "local_tax_completion";
    public const string BusinessRegistration = "business_registration";
    public const string BankbookCopy = "bankbook_copy";
    public const string ElectronicTaxInvoice = "electronic_tax_invoice";

    public static IReadOnlyList<AttachmentDocumentDefinition> OrderedDocuments { get; } =
    [
        new(Statement, "거래명세서"),
        new(Estimate, "견적서"),
        new(PaymentClaim, "대금청구서"),
        new(NationalTaxCompletion, "국세완납증명서"),
        new(LocalTaxCompletion, "지방세 완납증명서"),
        new(BusinessRegistration, "사업자 등록증"),
        new(BankbookCopy, "통장사본"),
        new(ElectronicTaxInvoice, "세금계산서(전자발행)")
    ];

    public static string GetDisplayName(string code)
    {
        return OrderedDocuments.FirstOrDefault(d =>
            string.Equals(d.Code, code, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? code;
    }
}
