namespace SalesMaster.Desktop.App.Services;

public sealed record PrintTemplateOption(
    string Id,
    string DisplayName,
    string TemplatePath,
    bool IsBuiltIn,
    NativeStatementLayoutType? BuiltInLayout = null);
