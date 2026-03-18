using System.Windows.Documents;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;

namespace 거래플랜.Desktop.App.Services;

public interface IPrintService
{
    InvoicePrintModel CreateDefaultModel(
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice);

    FixedDocument BuildFixedDocument(InvoicePrintModel model);

    bool TryPrint(
        FixedDocument document,
        string jobName,
        out string? errorMessage);
}
