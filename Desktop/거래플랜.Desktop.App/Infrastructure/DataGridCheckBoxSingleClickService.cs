using System.ComponentModel;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.Infrastructure;

public static class DataGridCheckBoxSingleClickService
{
    private static int _registered;

    public static void RegisterGlobal()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 1)
            return;

        EventManager.RegisterClassHandler(
            typeof(DataGridCell),
            UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnDataGridCellPreviewMouseLeftButtonDown),
            handledEventsToo: true);
    }

    private static void OnDataGridCellPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            sender is not DataGridCell cell ||
            cell.Column is not DataGridCheckBoxColumn checkBoxColumn)
        {
            return;
        }

        var dataGrid = FindVisualParent<DataGrid>(cell);
        if (dataGrid is null ||
            dataGrid.IsReadOnly ||
            checkBoxColumn.IsReadOnly ||
            cell.IsReadOnly)
        {
            return;
        }

        FocusCell(dataGrid, cell);

        if (!TryToggleBoundBoolean(cell.DataContext, checkBoxColumn.Binding))
            return;

        e.Handled = true;
    }

    private static void FocusCell(DataGrid dataGrid, DataGridCell cell)
    {
        if (!dataGrid.IsKeyboardFocusWithin)
            dataGrid.Focus();

        if (!cell.IsKeyboardFocusWithin)
            cell.Focus();

        if (cell.DataContext is not null && cell.Column is not null)
            dataGrid.CurrentCell = new DataGridCellInfo(cell.DataContext, cell.Column);

        var row = FindVisualParent<DataGridRow>(cell);
        if (row is not null &&
            row.Item is not null &&
            dataGrid.SelectionUnit == DataGridSelectionUnit.FullRow)
        {
            row.IsSelected = true;
        }
        else if (dataGrid.SelectionUnit != DataGridSelectionUnit.FullRow)
        {
            cell.IsSelected = true;
        }
    }

    private static bool TryToggleBoundBoolean(object? source, BindingBase? bindingBase)
    {
        if (source is null || bindingBase is not Binding binding)
            return false;

        if (binding.Mode is BindingMode.OneWay or BindingMode.OneTime ||
            binding.Converter is not null)
        {
            return false;
        }

        var propertyPath = binding.Path?.Path;
        if (string.IsNullOrWhiteSpace(propertyPath) ||
            propertyPath.Contains('.', StringComparison.Ordinal) ||
            propertyPath.Contains('[', StringComparison.Ordinal) ||
            propertyPath == ".")
        {
            return false;
        }

        var propertyDescriptor = TypeDescriptor.GetProperties(source).Find(propertyPath, ignoreCase: false);
        if (propertyDescriptor is null ||
            propertyDescriptor.IsReadOnly ||
            !IsBooleanProperty(propertyDescriptor.PropertyType))
        {
            return false;
        }

        try
        {
            var current = propertyDescriptor.GetValue(source);
            var next = current is bool currentBool ? !currentBool : true;
            propertyDescriptor.SetValue(source, next);
            return true;
        }
        catch (Exception ex)
        {
            AppLogger.Warn("UI", $"DataGrid 체크박스 단일 클릭 처리 실패: {ex.Message}");
            return false;
        }
    }

    private static bool IsBooleanProperty(Type propertyType)
        => propertyType == typeof(bool) || Nullable.GetUnderlyingType(propertyType) == typeof(bool);

    private static T? FindVisualParent<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null)
        {
            if (source is T match)
                return match;

            source = VisualTreeHelper.GetParent(source);
        }

        return null;
    }
}
