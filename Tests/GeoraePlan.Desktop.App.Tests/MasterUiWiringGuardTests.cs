using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class MasterUiWiringGuardTests
{
    [Fact]
    public void CustomerManagementWindow_WiresVisibleActionsToGuardedCustomerServices()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "CustomerManagementWindow.xaml");
        var code = ReadAppFile(appRoot, "Views", "CustomerManagementWindow.xaml.cs");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "CustomerManagementViewModel.cs");

        AssertContainsAll(
            xaml,
            "Click=\"CreateCustomerButton_Click\"",
            "Click=\"EditCustomerButton_Click\"",
            "Click=\"DeleteCustomerButton_Click\"",
            "Command=\"{Binding SaveOfficeChangesCommand}\"",
            "SelectionChanged=\"ResponsibleOfficeComboBox_SelectionChanged\"",
            "SelectedItem=\"{Binding ResponsibleOfficeCode, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"");

        AssertContainsAll(
            code,
            "new CustomerEditViewModel(_local, _session, _api)",
            "await customerVm.LoadAsync(_vm.SelectedCustomer.Source)",
            "() => _vm.SaveOfficeChangeAsync(row)",
            "await _local.DeleteCustomerAsync(selectedCustomer.Id, _session, selectedCustomer.Source.Revision)",
            "MessageBox.Show(result.Message, \"거래처 삭제\"");

        AssertContainsAll(
            viewModel,
            "await _local.GetCustomersAsync(_session)",
            ".Where(code => readableOfficeCodes.Contains(code))",
            "row.ApplyToSource()",
            "await _local.UpsertCustomerAsync(row.Source, _session)",
            "row.RestoreSavedOfficeCode()",
            "row.AcceptChanges()");
    }

    [Fact]
    public void CustomerEditWindow_SaveButtonsUseCustomerEditViewModelGuardedUpsert()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "CustomerEditWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "CustomerEditViewModel.cs");

        AssertContainsAll(
            xaml,
            "Command=\"{Binding SaveAndNewCommand}\"",
            "Command=\"{Binding SaveCommand}\"",
            "SelectedItem=\"{Binding ResponsibleOfficeCode}\"");

        AssertContainsAll(
            viewModel,
            "private async Task SaveAsync()",
            "private async Task SaveAndNewAsync()",
            "public async Task<bool> TryAutoSaveOnCloseAsync()",
            "ResponsibleOfficeCode = NormalizeOfficeCode(ResponsibleOfficeCode)",
            "var result = await _local.UpsertCustomerAsync(customer, _session)",
            "result.ConcurrencyConflict");
    }

    [Fact]
    public void InventoryWindow_ItemActionsUseScopedItemCommandsAndGuardedServices()
    {
        var appRoot = FindDesktopAppRoot();
        var xaml = ReadAppFile(appRoot, "Views", "InventoryWindow.xaml");
        var viewModel = ReadAppFile(appRoot, "ViewModels", "InventoryViewModel.cs");

        AssertContainsAll(
            xaml,
            "Command=\"{Binding NewItemCommand}\"",
            "Command=\"{Binding SaveItemCommand}\"",
            "Command=\"{Binding DeleteItemCommand}\"",
            "Command=\"{Binding ShowUsenetOfficeCommand}\"",
            "Command=\"{Binding ShowItworldOfficeCommand}\"",
            "Command=\"{Binding ShowYeonsuOfficeCommand}\"");

        AssertContainsAll(
            viewModel,
            "public bool CanDeleteSelectedItem =>",
            "_local.CanWriteItemScope(SelectedItem.Source, _session)",
            "[RelayCommand(CanExecute = nameof(CanDeleteSelectedItem))]",
            "var deleteResult = await _local.DeleteItemAsync(SelectedItem.Id, _session, SelectedItem.Source.Revision)",
            "if (!CanSaveItems)",
            "catch (UnauthorizedAccessException ex)",
            "await _local.UpsertItemAsync(BuildItem(snapshot), _session, snapshot.PreferredOfficeCode)",
            "var allItems = await _local.GetItemsAsync(_session)");
    }

    private static void AssertContainsAll(string source, params string[] expectedMarkers)
    {
        foreach (var marker in expectedMarkers)
            Assert.Contains(marker, source, StringComparison.Ordinal);
    }

    private static string ReadAppFile(string appRoot, params string[] pathParts)
        => File.ReadAllText(Path.Combine([appRoot, .. pathParts]));

    private static string FindDesktopAppRoot()
    {
        var root = FindRepositoryRoot();
        return Directory.EnumerateDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App", SearchOption.TopDirectoryOnly)
            .Single();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "Desktop")) &&
                Directory.EnumerateFiles(directory.FullName, "*.sln", SearchOption.TopDirectoryOnly).Any())
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
