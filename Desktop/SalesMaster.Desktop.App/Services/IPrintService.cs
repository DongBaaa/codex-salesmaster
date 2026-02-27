using System.Windows.Documents;
using SalesMaster.Desktop.App.Data;
using SalesMaster.Desktop.App.Printing;

namespace SalesMaster.Desktop.App.Services;

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
