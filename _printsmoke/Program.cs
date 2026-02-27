using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Services;
using SalesMaster.Shared.Contracts;

var templatePath = args.Length > 0
    ? args[0]
    : @"d:\새 폴더\클로드 레거시 판매관리\양식\_tmp_map_utf8.frx";

var customer = new LocalCustomer
{
    NameOriginal = "AA",
    BusinessNumber = "121-83-00724",
    Representative = "홍길동",
    Phone = "032-111-2222",
    FaxNumber = "032-111-3333",
    Address = "인천 미추홀구",
    DetailAddress = "101호",
    BusinessType = "도매",
    BusinessItem = "토너"
};

var company = new LocalCompanyProfile
{
    TradeName = "코덱스 레거시 판매관리",
    BusinessNumber = "[REDACTED_BUSINESS_NUMBER]",
    Representative = "관리자",
    ContactNumber = "032-000-0000",
    Address = "인천시 미추홀구 경인로 221",
    BusinessType = "도매 및 소매업",
    BusinessItem = "컴퓨터 및 주변 기기",
    BankAccountText = "신한 110-597-57945"
};

var invoice = new LocalInvoice
{
    InvoiceNumber = "1013-01-2043827",
    LocalTempNumber = "TMP-0001",
    VoucherType = VoucherType.Sales,
    InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
    Memo = "테스트",
    TotalAmount = 330000m,
    SupplyAmount = 300000m,
    VatAmount = 30000m,
    Lines = new List<LocalInvoiceLine>()
    {
        new LocalInvoiceLine
        {
            ItemNameOriginal = "사무기기 렌탈대금(2월)",
            SpecificationOriginal = "리코 IMC2000",
            Unit = "1",
            Quantity = 1,
            UnitPrice = 330000,
            LineAmount = 330000,
            MaterialNumber = "MAT-01",
            Remark = ""
        }
    },
    Payments = new List<LocalPayment>()
    {
        new LocalPayment { Amount = 0 }
    }
};

var service = new 외부 리포팅 도구TemplatePrintService();

try
{
    var ok = service.ShowPreviewAndPrint(
        templatePath,
        invoice,
        customer,
        company,
        printWithDate: true,
        printWithPrice: true,
        jobName: "smoke_test");

    Console.WriteLine("RESULT=" + ok);
}
catch (Exception ex)
{
    Console.WriteLine("EXCEPTION");
    Console.WriteLine(ex.ToString());
}

