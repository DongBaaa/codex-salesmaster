namespace SalesMaster.Desktop.App.Services;

public sealed class RentalContractDocumentModel
{
    public string Title { get; set; } = "사무기기 렌탈 계약서";
    public string TenantName { get; set; } = string.Empty;
    public string TenantBusinessNumber { get; set; } = string.Empty;
    public string TenantAddress { get; set; } = string.Empty;
    public string TenantRepresentative { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string CompanyBusinessNumber { get; set; } = string.Empty;
    public string CompanyAddress { get; set; } = string.Empty;
    public string CompanyRepresentative { get; set; } = string.Empty;
    public string CompanyContactNumber { get; set; } = string.Empty;
    public string CompanyFaxNumber { get; set; } = string.Empty;
    public string ManagementNumber { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public string MachineNumber { get; set; } = string.Empty;
    public string DepositText { get; set; } = string.Empty;
    public decimal MonthlyFee { get; set; }
    public string InstallLocation { get; set; } = string.Empty;
    public DateOnly ContractDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly ContractStartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? ContractEndDate { get; set; }
    public string IntroText { get; set; } = string.Empty;
    public List<RentalContractClauseModel> Clauses { get; } = new();
    public List<string> SpecialTerms { get; } = new();
    public string ClosingLine1 { get; set; } = string.Empty;
    public string ClosingLine2 { get; set; } = string.Empty;
    public string FooterBuyerLabel { get; set; } = "발주업체";
    public string FooterCompanyLabel { get; set; } = "렌탈업체";
    public byte[]? CompanyStampImage { get; set; }
}

public sealed class RentalContractClauseModel
{
    public string Number { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
}
