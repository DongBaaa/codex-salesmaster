using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed class InvoiceOfficeFilterOption
{
    public string Code { get; init; } = OfficeCodeCatalog.Shared;
    public string DisplayName { get; init; } = "전체";
}

public sealed partial class MainViewModel
{
    public ObservableCollection<InvoiceOfficeFilterOption> InvoiceOfficeFilterOptions { get; } = new();

    [ObservableProperty]
    private string _selectedInvoiceOfficeFilterCode = OfficeCodeCatalog.Shared;

    partial void OnSelectedInvoiceOfficeFilterCodeChanged(string value)
        => HandleInvoiceFilterChanged();

    private string BuildAccountScopedInvoiceFilterKey(string baseKey)
    {
        var username = (_session.User?.Username ?? "local").Trim().ToLowerInvariant();
        var databaseName = TenantScopeCatalog.GetDatabaseName(_session.SelectedBusinessDatabaseName);
        return $"{baseKey}.{databaseName}.{username}";
    }

    private IReadOnlyList<string> GetReadableInvoiceOfficeCodesForFilter()
    {
        if (_session.HasGlobalDataScope ||
            string.Equals(_session.ScopeType, TenantScopeCatalog.ScopeTenantAll, StringComparison.OrdinalIgnoreCase))
        {
            return TenantScopeCatalog.GetOfficeCodesForTenant(_session.TenantCode);
        }

        return
        [
            OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, OfficeCodeCatalog.Usenet)
        ];
    }

    private void InitializeInvoiceOfficeFilterOptions()
    {
        var currentSelection = SelectedInvoiceOfficeFilterCode;
        var officeCodes = GetReadableInvoiceOfficeCodesForFilter();

        InvoiceOfficeFilterOptions.Clear();
        if (officeCodes.Count > 1)
        {
            InvoiceOfficeFilterOptions.Add(new InvoiceOfficeFilterOption
            {
                Code = OfficeCodeCatalog.Shared,
                DisplayName = "전체"
            });
        }

        foreach (var officeCode in officeCodes)
        {
            InvoiceOfficeFilterOptions.Add(new InvoiceOfficeFilterOption
            {
                Code = officeCode,
                DisplayName = OfficeCodeCatalog.GetOfficeDisplayName(officeCode)
            });
        }

        var defaultOfficeCode = GetDefaultInvoiceOfficeFilterCode();
        var normalizedSelection = OfficeCodeCatalog.IsSharedOfficeCode(currentSelection)
            ? defaultOfficeCode
            : OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(currentSelection, defaultOfficeCode);
        var hasMatchingOption = InvoiceOfficeFilterOptions.Any(option =>
            string.Equals(option.Code, normalizedSelection, StringComparison.OrdinalIgnoreCase));

        SelectedInvoiceOfficeFilterCode = hasMatchingOption
            ? normalizedSelection
            : defaultOfficeCode;
    }

    private string GetDefaultInvoiceOfficeFilterCode()
    {
        var officeCodes = GetReadableInvoiceOfficeCodesForFilter();
        var sessionOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, OfficeCodeCatalog.Usenet);

        return officeCodes.FirstOrDefault(officeCode =>
                   string.Equals(officeCode, sessionOfficeCode, StringComparison.OrdinalIgnoreCase))
               ?? officeCodes.FirstOrDefault()
               ?? sessionOfficeCode;
    }

    private bool MatchesSelectedInvoiceOffice(LocalInvoice invoice)
    {
        var selectedOfficeCode = OfficeCodeCatalog.NormalizeOfficeScopeOrDefault(
            SelectedInvoiceOfficeFilterCode,
            GetDefaultInvoiceOfficeFilterCode());

        if (OfficeCodeCatalog.IsSharedOfficeCode(selectedOfficeCode))
            return true;

        var invoiceOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(
            invoice.ResponsibleOfficeCode,
            _session.OfficeCode);

        return string.Equals(invoiceOfficeCode, selectedOfficeCode, StringComparison.OrdinalIgnoreCase);
    }
}
