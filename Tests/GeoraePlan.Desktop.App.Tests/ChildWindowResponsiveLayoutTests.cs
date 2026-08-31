using System.Globalization;
using System.Windows;
using System.Xml.Linq;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Views;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ChildWindowResponsiveLayoutTests
{
    public static TheoryData<double, double, double> RequiredWorkAreas =>
        new()
        {
            { 1920d, 1040d, 1.00d },
            { 1920d, 1040d, 1.25d },
            { 1920d, 1040d, 1.50d },
            { 1366d, 728d, 1.00d },
            { 1366d, 728d, 1.25d },
            { 1366d, 728d, 1.50d }
        };

    [Theory]
    [MemberData(nameof(RequiredWorkAreas))]
    public void CoreChildWindows_FitInsideRequiredPhysicalWorkAreas(
        double physicalWidth,
        double physicalHeight,
        double scale)
    {
        var physicalWorkArea = new Rect(
            0d,
            0d,
            physicalWidth,
            physicalHeight);
        var minimum = new Size(
            ChildWindowResponsiveLayoutPolicy.MinimumWidthDip,
            ChildWindowResponsiveLayoutPolicy.MinimumHeightDip);
        var preferredSizes = new[]
        {
            new Size(860d, 960d),
            new Size(1220d, 900d),
            new Size(1640d, 940d),
            new Size(1680d, 960d)
        };

        foreach (var preferred in preferredSizes)
        {
            var result = ChildWindowResponsiveLayoutPolicy
                .ResolvePhysicalWindowBounds(
                    physicalWorkArea,
                    scale,
                    preferred,
                    minimum);

            Assert.False(result.IsEmpty);
            Assert.True(result.Left >= physicalWorkArea.Left);
            Assert.True(result.Top >= physicalWorkArea.Top);
            Assert.True(result.Right <= physicalWorkArea.Right);
            Assert.True(result.Bottom <= physicalWorkArea.Bottom);
            Assert.True(result.Width <= physicalWorkArea.Width);
            Assert.True(result.Height <= physicalWorkArea.Height);
            Assert.Equal(
                physicalWorkArea.Left +
                ((physicalWorkArea.Width - result.Width) / 2d),
                result.Left,
                precision: 6);
            Assert.Equal(
                physicalWorkArea.Top +
                ((physicalWorkArea.Height - result.Height) / 2d),
                result.Top,
                precision: 6);
        }
    }

    [Fact]
    public void PhysicalBounds_StayInsideNegativeOffsetOwnerMonitor()
    {
        var physicalWorkArea = new Rect(-2560d, -120d, 1366d, 728d);

        var result = ChildWindowResponsiveLayoutPolicy
            .ResolvePhysicalWindowBounds(
                physicalWorkArea,
                1.5d,
                new Size(1680d, 960d),
                new Size(640d, 400d));

        Assert.False(result.IsEmpty);
        Assert.True(result.Left >= physicalWorkArea.Left);
        Assert.True(result.Top >= physicalWorkArea.Top);
        Assert.True(result.Right <= physicalWorkArea.Right);
        Assert.True(result.Bottom <= physicalWorkArea.Bottom);
    }

    [Fact]
    public void SmallerThanMinimumWorkArea_RelaxesMinimumWithoutOverflow()
    {
        var logicalWorkArea = new Rect(0d, 0d, 600d, 300d);

        var result = ChildWindowResponsiveLayoutPolicy
            .ResolveInitialWindowSize(
                new Size(1680d, 960d),
                new Size(640d, 400d),
                logicalWorkArea);

        Assert.Equal(
            logicalWorkArea.Width -
            ChildWindowResponsiveLayoutPolicy.WorkAreaInsetDip,
            result.Width,
            precision: 6);
        Assert.Equal(
            logicalWorkArea.Height -
            ChildWindowResponsiveLayoutPolicy.WorkAreaInsetDip,
            result.Height,
            precision: 6);
    }

    [Fact]
    public void InvalidWorkArea_PreservesDeclaredPreferredSize()
    {
        var result = ChildWindowResponsiveLayoutPolicy
            .ResolveInitialWindowSize(
                new Size(1220d, 900d),
                new Size(640d, 400d),
                Rect.Empty);

        Assert.Equal(1220d, result.Width);
        Assert.Equal(900d, result.Height);
    }

    [Fact]
    public void CoreChildWindowXaml_KeepsActionsReachableAndBodyScrollScoped()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var customer = LoadWindow(desktopAppDirectory, "CustomerEditWindow.xaml");
        AssertResponsiveMinimum(customer.Root);
        AssertScrollViewer(
            customer,
            xaml,
            "CustomerBodyScrollViewer",
            horizontal: "Disabled",
            vertical: "Auto");
        AssertNamedElementAttribute(
            customer,
            xaml,
            "CustomerBodyContent",
            "MinWidth",
            "780");

        var inventory = LoadWindow(desktopAppDirectory, "InventoryWindow.xaml");
        AssertResponsiveMinimum(inventory.Root);
        AssertScrollViewer(
            inventory,
            xaml,
            "InventoryDetailScrollViewer",
            horizontal: "Disabled",
            vertical: "Auto");
        AssertNamedElementAttribute(
            inventory,
            xaml,
            "InventoryDetailContent",
            "MinWidth",
            "650");
        AssertInventoryDetailWorkspaceRows(inventory, xaml);
        Assert.Contains(
            inventory.Descendants(),
            element =>
                element.Name.LocalName == "WrapPanel" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "1",
                    StringComparison.Ordinal) &&
                element.Descendants().Any(button =>
                    button.Name.LocalName == "Button" &&
                    string.Equals(
                        (string?)button.Attribute("Content"),
                        "품목 저장",
                        StringComparison.Ordinal)));
        var inventoryMemo = Assert.Single(
            inventory.Descendants(),
            element =>
                element.Name.LocalName == "TextBox" &&
                ((string?)element.Attribute("Text"))?.Contains(
                    "EditSimpleMemo",
                    StringComparison.Ordinal) == true);
        Assert.Equal("4", (string?)inventoryMemo.Attribute("Grid.Row"));
        Assert.Equal("1", (string?)inventoryMemo.Attribute("Grid.Column"));
        Assert.Equal("3", (string?)inventoryMemo.Attribute("Grid.ColumnSpan"));
        Assert.Equal("64", (string?)inventoryMemo.Attribute("Height"));
        Assert.Equal("64", (string?)inventoryMemo.Attribute("MinHeight"));
        Assert.Equal("True", (string?)inventoryMemo.Attribute("AcceptsReturn"));
        Assert.Equal("Wrap", (string?)inventoryMemo.Attribute("TextWrapping"));
        Assert.Equal("Auto", (string?)inventoryMemo.Attribute("VerticalScrollBarVisibility"));

        var rentalAsset = LoadWindow(
            desktopAppDirectory,
            "RentalAssetWindow.xaml");
        AssertResponsiveMinimum(rentalAsset.Root);
        AssertCommandAndWorkspaceRows(rentalAsset);
        var rentalAssetCommandPanel = AssertNamedElement(
            rentalAsset,
            xaml,
            "RentalAssetCommandPanel");
        Assert.Equal("StackPanel", rentalAssetCommandPanel.Name.LocalName);
        Assert.DoesNotContain(
            rentalAssetCommandPanel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        AssertScrollViewer(
            rentalAsset,
            xaml,
            "RentalAssetDetailScrollViewer",
            horizontal: "Disabled",
            vertical: "Auto");
        AssertNamedElementAttribute(
            rentalAsset,
            xaml,
            "RentalAssetDetailContent",
            "MinWidth",
            "620");

        var rentalBilling = LoadWindow(
            desktopAppDirectory,
            "RentalBillingWindow.xaml");
        AssertResponsiveMinimum(rentalBilling.Root);
        AssertCommandAndWorkspaceRows(rentalBilling);
        var billingCommandPanel = AssertNamedElement(
            rentalBilling,
            xaml,
            "BillingCommandPanel");
        Assert.Equal("StackPanel", billingCommandPanel.Name.LocalName);
        Assert.DoesNotContain(
            billingCommandPanel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        Assert.DoesNotContain(
            rentalBilling.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "BillingWorkspaceScrollViewer",
                StringComparison.Ordinal));
        var billingWorkspace = AssertNamedElement(
            rentalBilling,
            xaml,
            "BillingWorkspaceGrid");
        Assert.Null((string?)billingWorkspace.Attribute("MinWidth"));
    }

    [Fact]
    public void AllProductionWindowXaml_AvoidsCompetingScrollOwnersAndUnboundedFixedPanels()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        var scrollableControlNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "DataGrid", "ListBox", "ListView", "TreeView", "ScrollViewer"
        };
        var competingHorizontalOwners = new List<string>();
        var nestedScrollOwners = new List<string>();
        var unboundedFixedPanels = new List<string>();
        var fixedHeightActionButtonStyles = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
                     desktopAppDirectory,
                     "*.xaml",
                     SearchOption.AllDirectories))
        {
            var document = XDocument.Load(path);
            if (!string.Equals(document.Root?.Name.LocalName, "Window", StringComparison.Ordinal))
                continue;

            var relativePath = Path.GetRelativePath(desktopAppDirectory, path);
            foreach (var scrollViewer in document.Descendants().Where(
                         element => element.Name.LocalName == "ScrollViewer"))
            {
                if (string.Equals(
                        (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"),
                        "Auto",
                        StringComparison.Ordinal) &&
                    scrollViewer.Descendants().Any(
                        descendant => scrollableControlNames.Contains(descendant.Name.LocalName)))
                {
                    competingHorizontalOwners.Add(relativePath);
                }

                if (scrollViewer.Ancestors().Any(
                        ancestor => ancestor.Name.LocalName == "ScrollViewer") &&
                    !scrollViewer.Ancestors().Any(
                        ancestor => ancestor.Name.LocalName == "Popup") &&
                    !string.Equals(relativePath, "MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
                {
                    nestedScrollOwners.Add(relativePath);
                }
            }

            foreach (var panel in document.Descendants().Where(
                         element => element.Name.LocalName is "Grid" or "StackPanel" or "DockPanel"))
            {
                if (!double.TryParse(
                        (string?)panel.Attribute("MinWidth"),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var minWidth) ||
                    minWidth < 600d ||
                    panel.Ancestors().Any(ancestor => ancestor.Name.LocalName == "ScrollViewer"))
                {
                    continue;
                }

                unboundedFixedPanels.Add(relativePath);
            }

            foreach (var style in document.Descendants().Where(
                         element => element.Name.LocalName == "Style"))
            {
                var targetType = (string?)style.Attribute("TargetType");
                if (string.IsNullOrWhiteSpace(targetType) ||
                    !targetType.Contains("Button", StringComparison.Ordinal) ||
                    targetType.Contains("ToggleButton", StringComparison.Ordinal))
                {
                    continue;
                }

                var styleKey = style.Attributes()
                    .FirstOrDefault(attribute => attribute.Name.LocalName == "Key")
                    ?.Value;
                if (styleKey?.Contains("DatePicker", StringComparison.OrdinalIgnoreCase) is true)
                    continue;

                if (style.Elements().Any(setter =>
                        setter.Name.LocalName == "Setter" &&
                        string.Equals(
                            (string?)setter.Attribute("Property"),
                            "Height",
                            StringComparison.Ordinal)))
                {
                    fixedHeightActionButtonStyles.Add(relativePath);
                }
            }
        }

        Assert.Empty(competingHorizontalOwners.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(nestedScrollOwners.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(unboundedFixedPanels.Distinct(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(fixedHeightActionButtonStyles.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void RemainingModalWindows_KeepScrollableBodyAndFixedFooter()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var modal in new[]
                 {
                     new
                     {
                         FileName = "AttachmentSelectionWindow.xaml",
                         ScrollViewerName = "AttachmentSelectionItemsScrollViewer",
                         ContentName = "AttachmentSelectionItemsContent",
                         FooterName = "AttachmentSelectionFooter",
                         MinimumContentWidth = 520d
                     },
                     new
                     {
                         FileName = "RentalAssignmentHistoryEditWindow.xaml",
                         ScrollViewerName = "RentalAssignmentHistoryBodyScrollViewer",
                         ContentName = "RentalAssignmentHistoryBodyContent",
                         FooterName = "RentalAssignmentHistoryFooter",
                         MinimumContentWidth = 520d
                     },
                     new
                     {
                         FileName = "RentalEquipmentReplacementWindow.xaml",
                         ScrollViewerName = "RentalEquipmentReplacementBodyScrollViewer",
                         ContentName = "RentalEquipmentReplacementBodyContent",
                         FooterName = "RentalEquipmentReplacementFooter",
                         MinimumContentWidth = 540d
                     },
                     new
                     {
                         FileName = "RentalReturnReportInputWindow.xaml",
                         ScrollViewerName = "RentalReturnReportBodyScrollViewer",
                         ContentName = "RentalReturnReportBodyContent",
                         FooterName = "RentalReturnReportFooter",
                         MinimumContentWidth = 500d
                     }
                 })
        {
            var document = LoadWindow(
                desktopAppDirectory,
                modal.FileName);
            AssertResponsiveModalShell(
                document,
                xaml,
                modal.ScrollViewerName,
                modal.ContentName,
                modal.FooterName,
                modal.MinimumContentWidth);
        }

        var attachment = LoadWindow(
            desktopAppDirectory,
            "AttachmentSelectionWindow.xaml");
        var attachmentItems = AssertNamedElement(
            attachment,
            xaml,
            "AttachmentSelectionItemsContent");
        var attachmentFooter = AssertNamedElement(
            attachment,
            xaml,
            "AttachmentSelectionFooter");
        Assert.Equal(
            "{Binding Items}",
            (string?)attachmentItems.Attribute("ItemsSource"));
        Assert.Contains(
            attachmentItems.Descendants(),
            element =>
                element.Name.LocalName == "UniformGrid" &&
                string.Equals(
                    (string?)element.Attribute("Columns"),
                    "2",
                    StringComparison.Ordinal));
        foreach (var command in new[]
                 {
                     "{Binding ResetOrderCommand}",
                     "{Binding ConfirmCommand}",
                     "{Binding CancelCommand}"
                 })
        {
            Assert.Contains(
                attachmentFooter.Descendants(),
                element =>
                    element.Name.LocalName == "Button" &&
                    string.Equals(
                        (string?)element.Attribute("Command"),
                        command,
                        StringComparison.Ordinal));
        }

        var assignment = LoadWindow(
            desktopAppDirectory,
            "RentalAssignmentHistoryEditWindow.xaml");
        var assignmentFooter = AssertNamedElement(
            assignment,
            xaml,
            "RentalAssignmentHistoryFooter");
        Assert.Contains(
            assignmentFooter.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Click"),
                    "Save_Click",
                    StringComparison.Ordinal));
        Assert.Contains(
            assignmentFooter.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Click"),
                    "Cancel_Click",
                    StringComparison.Ordinal));

        var replacement = LoadWindow(
            desktopAppDirectory,
            "RentalEquipmentReplacementWindow.xaml");
        var replacementFooter = AssertNamedElement(
            replacement,
            xaml,
            "RentalEquipmentReplacementFooter");
        foreach (var name in new[]
                 {
                     "OriginalSummaryText",
                     "OriginalDetailText",
                     "ReplacementSummaryText",
                     "ReplacementDetailText",
                     "ReplacementDatePicker",
                     "OriginalStatusBox",
                     "ReasonBox"
                 })
        {
            _ = AssertNamedElement(replacement, xaml, name);
        }
        Assert.Contains(
            replacementFooter.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Click"),
                    "Confirm_Click",
                    StringComparison.Ordinal));
        Assert.Contains(
            replacementFooter.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Click"),
                    "Cancel_Click",
                    StringComparison.Ordinal));

        var returnReport = LoadWindow(
            desktopAppDirectory,
            "RentalReturnReportInputWindow.xaml");
        var returnReportContent = AssertNamedElement(
            returnReport,
            xaml,
            "RentalReturnReportBodyContent");
        var returnReportFooter = AssertNamedElement(
            returnReport,
            xaml,
            "RentalReturnReportFooter");
        Assert.Equal(
            "{Binding ViewportHeight, ElementName=RentalReturnReportBodyScrollViewer}",
            (string?)returnReportContent.Attribute("MinHeight"));
        var returnReportRows = Assert.Single(
                returnReportContent.Elements(),
                element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "10", "*" }, returnReportRows);
        _ = AssertNamedElement(returnReport, xaml, "ReturnReasonBox");
        var faultDescription = AssertNamedElement(
            returnReport,
            xaml,
            "FaultDescriptionBox");
        var faultGrid = Assert.IsType<XElement>(faultDescription.Parent);
        var faultRows = Assert.Single(
                faultGrid.Elements(),
                element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "*" }, faultRows);
        Assert.Contains(
            returnReportFooter.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Click"),
                    "Confirm_Click",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)element.Attribute("IsDefault"),
                    "True",
                    StringComparison.Ordinal));
        Assert.Contains(
            returnReportFooter.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("IsCancel"),
                    "True",
                    StringComparison.Ordinal));
    }

    [Fact]
    public void SalesWindow_KeepsHeadersReachableAndDataGridsVirtualizable()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var sales = LoadWindow(desktopAppDirectory, "SalesWindow.xaml");

        AssertWindowMinimum(sales.Root, expectedWidth: 800d, expectedHeight: 460d);
        AssertScrollViewer(
            sales,
            xaml,
            "SalesCustomerHeaderScrollViewer",
            horizontal: "Auto",
            vertical: "Disabled");
        var customerHeaderScrollViewer = AssertNamedElement(
            sales,
            xaml,
            "SalesCustomerHeaderScrollViewer");
        Assert.Null(customerHeaderScrollViewer.Attribute("MaxHeight"));
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesCustomerHeaderContent",
            "MinWidth",
            "880");
        AssertScrollViewer(
            sales,
            xaml,
            "SalesDocumentHeaderScrollViewer",
            horizontal: "Auto",
            vertical: "Disabled");
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesDocumentHeaderContent",
            "MinWidth",
            "780");
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesDocumentHeaderContent",
            "MinHeight",
            "38");
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesLoadPreviousHistoryButton",
            "Height",
            "36");
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesLoadPreviousHistoryButton",
            "MinHeight",
            "36");
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesLoadPreviousHistoryButton",
            "VerticalContentAlignment",
            "Center");
        AssertScrollViewer(
            sales,
            xaml,
            "SalesLineEntryScrollViewer",
            horizontal: "Auto",
            vertical: "Disabled");
        AssertNamedElementAttribute(
            sales,
            xaml,
            "SalesLineEntryContent",
            "MinWidth",
            "1160");

        var lineActionButtons = sales
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Button" &&
                ((string?)element.Attribute("Content") is
                    "항목추가" or "항목수정" or "항목삭제"))
            .ToArray();
        Assert.Equal(3, lineActionButtons.Length);
        Assert.All(
            lineActionButtons,
            button =>
            {
                Assert.Equal("72", (string?)button.Attribute("Width"));
                Assert.Equal("72", (string?)button.Attribute("MinWidth"));
                Assert.Equal("4,0", (string?)button.Attribute("Padding"));
            });
        var lineMoveButtons = sales
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Button" &&
                ((string?)element.Attribute("Content") is "▲" or "▼"))
            .ToArray();
        Assert.Equal(2, lineMoveButtons.Length);
        Assert.All(lineMoveButtons, button =>
            Assert.Equal("30", (string?)button.Attribute("Width")));

        AssertNamedElementType(sales, xaml, "SalesHeaderActions", "WrapPanel");
        AssertNamedElementType(sales, xaml, "SalesItemSearchToolbar", "WrapPanel");
        AssertSalesHeaderActions(sales, xaml);
        AssertDataGridVirtualizationStyle(sales);
        AssertSalesWorkspaceRows(sales, xaml);
        AssertSalesCompactSectionSwitcher(sales, xaml);
    }

    [Fact]
    public void MainTransactionAndSalesItemGrids_ReuseTheSameDataRowStyle()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var app = XDocument.Load(Path.Combine(desktopAppDirectory, "App.xaml"));
        var main = XDocument.Load(Path.Combine(desktopAppDirectory, "MainWindow.xaml"));
        var sales = LoadWindow(desktopAppDirectory, "SalesWindow.xaml");

        var sharedRowStyle = Assert.Single(
            app.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Key"),
                    "MainTransactionDataRowStyle",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            sharedRowStyle.Elements(),
            element =>
                element.Name.LocalName == "Setter" &&
                ((string?)element.Attribute("Property") is "Height" or "MinHeight"));

        var sharedGridStyle = Assert.Single(
            app.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Key"),
                    "MainTransactionDataGridStyle",
                    StringComparison.Ordinal));
        Assert.Contains(
            sharedGridStyle.Elements(),
            element =>
                element.Name.LocalName == "Setter" &&
                string.Equals((string?)element.Attribute("Property"), "RowHeight", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("Value"), "NaN", StringComparison.Ordinal));
        Assert.Contains(
            sharedGridStyle.Elements(),
            element =>
                element.Name.LocalName == "Setter" &&
                string.Equals((string?)element.Attribute("Property"), "MinRowHeight", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("Value"), "0", StringComparison.Ordinal));

        foreach (var window in new[] { main, sales })
        {
            var localGridStyle = Assert.Single(
                window.Descendants().First(element => element.Name.LocalName == "Window.Resources").Elements(),
                element =>
                    element.Name.LocalName == "Style" &&
                    string.Equals((string?)element.Attribute("TargetType"), "DataGrid", StringComparison.Ordinal) &&
                    element.Attribute(xaml + "Key") is null);
            Assert.Equal(
                "{StaticResource MainTransactionDataGridStyle}",
                (string?)localGridStyle.Attribute("BasedOn"));
        }

        var mainGrid = AssertNamedElement(main, xaml, "InvoiceRowsDataGrid");
        var mainRowStyle = Assert.Single(
            mainGrid.Descendants(),
            element => element.Name.LocalName == "Style" &&
                       string.Equals((string?)element.Attribute("TargetType"), "DataGridRow", StringComparison.Ordinal));
        Assert.Equal(
            "{StaticResource MainTransactionDataRowStyle}",
            (string?)mainRowStyle.Attribute("BasedOn"));

        foreach (var dataGridName in new[] { "SalesLinesDataGrid", "ItemSearchResultsDataGrid" })
        {
            var dataGrid = AssertNamedElement(sales, xaml, dataGridName);
            Assert.Equal(
                "{StaticResource MainTransactionDataRowStyle}",
                (string?)dataGrid.Attribute("RowStyle"));
            Assert.Contains(
                dataGrid.Attributes(),
                attribute =>
                    attribute.Name.LocalName.EndsWith(".PreserveSingleLine", StringComparison.Ordinal) &&
                    string.Equals(attribute.Value, "True", StringComparison.Ordinal));
        }

        var salesCellStyle = Assert.Single(
            sales.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                string.Equals((string?)element.Attribute("TargetType"), "DataGridCell", StringComparison.Ordinal) &&
                element.Attribute(xaml + "Key") is null);
        Assert.Contains(
            salesCellStyle.Elements(),
            element =>
                element.Name.LocalName == "Setter" &&
                string.Equals((string?)element.Attribute("Property"), "BorderThickness", StringComparison.Ordinal) &&
                string.Equals((string?)element.Attribute("Value"), "0,0,1,0", StringComparison.Ordinal));
    }

    [Fact]
    public void PaymentWindow_KeepsFixedActionsAndVirtualizedTabbedWorkspace()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var payment = LoadWindow(desktopAppDirectory, "PaymentWindow.xaml");

        AssertResponsiveMinimum(payment.Root);
        var paymentWindow = Assert.IsType<XElement>(payment.Root);
        Assert.Equal("1080", (string?)paymentWindow.Attribute("Width"));
        Assert.Equal("820", (string?)paymentWindow.Attribute("Height"));
        AssertScrollViewer(
            payment,
            xaml,
            "PaymentCommandScrollViewer",
            horizontal: "Auto",
            vertical: "Auto");
        AssertNamedElementAttribute(
            payment,
            xaml,
            "PaymentCommandContent",
            "MinWidth",
            "840");

        var commandScrollViewer = AssertNamedElement(
            payment,
            xaml,
            "PaymentCommandScrollViewer");
        foreach (var buttonName in new[]
                 {
                     "PaymentCloseButton",
                     "PaymentSaveButton",
                 })
        {
            var button = AssertNamedElement(payment, xaml, buttonName);
            Assert.Equal("Button", button.Name.LocalName);
            Assert.DoesNotContain(
                button.Ancestors(),
                ancestor => ReferenceEquals(ancestor, commandScrollViewer));
            Assert.DoesNotContain(
                button.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }

        foreach (var textName in new[]
                 {
                     "PaymentHeaderTitle",
                     "PaymentHeaderSubtitle",
                     "PaymentFooterStatus",
                 })
        {
            var text = AssertNamedElement(payment, xaml, textName);
            Assert.Equal("Wrap", (string?)text.Attribute("TextWrapping"));
            Assert.Equal(
                "None",
                (string?)text.Attribute("TextTrimming"));
            Assert.False(
                string.IsNullOrWhiteSpace(
                    (string?)text.Attribute("ToolTip")));
        }

        var contextSummary = AssertNamedElement(
            payment,
            xaml,
            "PaymentContextSummaryPanel");
        Assert.Equal("1", (string?)contextSummary.Attribute("Grid.Row"));
        Assert.Contains(
            contextSummary.Ancestors(),
            ancestor => string.Equals(
                (string?)ancestor.Attribute(xaml + "Name"),
                "PaymentCommandContent",
                StringComparison.Ordinal));

        AssertDataGridVirtualizationStyle(payment);
        AssertPaymentFixedHeaderFooter(payment, xaml);
        AssertPaymentBodyRows(payment, xaml);
        AssertPaymentWorkspaceTabs(payment, xaml);
        AssertPaymentCompactSectionSwitcher(payment, xaml);

        foreach (var summaryName in new[]
                 {
                     "PaymentTransactionContextSummaryText",
                     "PaymentTransactionSummaryText",
                 })
        {
            var summary = AssertNamedElement(payment, xaml, summaryName);
            Assert.False(
                string.IsNullOrWhiteSpace(
                    (string?)summary.Attribute("ToolTip")));
        }
    }

    [Fact]
    public void RentalCustomerOnboardingWindow_KeepsStepsFormsAndFooterReachable()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var onboarding = LoadWindow(
            desktopAppDirectory,
            "RentalCustomerOnboardingWindow.xaml");

        AssertResponsiveMinimum(onboarding.Root);
        var onboardingWindow = Assert.IsType<XElement>(onboarding.Root);
        Assert.Equal("1100", (string?)onboardingWindow.Attribute("Width"));
        Assert.Equal("820", (string?)onboardingWindow.Attribute("Height"));
        Assert.Equal("CanResize", (string?)onboardingWindow.Attribute("ResizeMode"));

        var sidebar = AssertNamedElement(
            onboarding,
            xaml,
            "OnboardingStepSidebar");
        var actualSteps = sidebar
            .Descendants()
            .Where(element => element.Name.LocalName == "TextBlock")
            .Select(element => (string?)element.Attribute("Text"))
            .Where(text =>
                text is { Length: > 1 } &&
                text[0] is >= '1' and <= '6' &&
                text[1] == '.')
            .ToArray();
        Assert.Equal(
            new[]
            {
                "1. 거래처정보",
                "2. 렌탈 기본정보",
                "3. 임대료 청구 설정",
                "4. 장비 연결",
                "5. 표시품목/거래처 임대 자산 구성",
                "6. 최종 확인",
            },
            actualSteps);

        var viewModelSource = File.ReadAllText(
            Path.Combine(
                desktopAppDirectory,
                "ViewModels",
                "RentalCustomerOnboardingViewModel.cs"));
        Assert.Contains(
            "4 => \"5. 표시품목/거래처 임대 자산 구성\"",
            viewModelSource,
            StringComparison.Ordinal);

        foreach (var scrollViewerName in new[]
                 {
                     "CustomerInfoScrollViewer",
                     "RentalBasicsScrollViewer",
                     "BillingSettingsScrollViewer",
                     "FinalReviewScrollViewer",
                 })
        {
            AssertScrollViewer(
                onboarding,
                xaml,
                scrollViewerName,
                horizontal: "Auto",
                vertical: "Auto");
            var scrollViewer = AssertNamedElement(
                onboarding,
                xaml,
                scrollViewerName);
            var scrollContent = Assert.Single(scrollViewer.Elements());
            Assert.Equal("Grid", scrollContent.Name.LocalName);
            Assert.Equal("760", (string?)scrollContent.Attribute("MinWidth"));
        }
        AssertScrollViewer(
            onboarding,
            xaml,
            "TemplateActionsScrollViewer",
            horizontal: "Auto",
            vertical: "Disabled");

        AssertDataGridVirtualizationStyle(onboarding);
        foreach (var dataGrid in onboarding.Descendants()
                     .Where(element => element.Name.LocalName == "DataGrid"))
        {
            Assert.DoesNotContain(
                dataGrid.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }

        var rootGrid = Assert.Single(
            onboardingWindow.Elements(),
            element => element.Name.LocalName == "Grid");
        var rowDefinitions = Assert.Single(
                rootGrid.Elements(),
                element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "*", "Auto" }, rowDefinitions);

        var footer = Assert.Single(
            rootGrid.Elements(),
            element =>
                element.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "2",
                    StringComparison.Ordinal));
        var footerButtons = footer
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();
        Assert.Contains(
            footerButtons,
            button => string.Equals(
                (string?)button.Attribute("Command"),
                "{Binding PreviousStepCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            footerButtons,
            button => string.Equals(
                (string?)button.Attribute("Command"),
                "{Binding NextStepCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            footerButtons,
            button => string.Equals(
                (string?)button.Attribute("Command"),
                "{Binding SaveCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            footerButtons,
            button => string.Equals(
                (string?)button.Attribute("Click"),
                "CancelButton_Click",
                StringComparison.Ordinal));
        foreach (var footerButton in footerButtons)
        {
            Assert.DoesNotContain(
                footerButton.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }

        var codeBehind = RemoveWhitespace(
            File.ReadAllText(
                Path.Combine(
                    desktopAppDirectory,
                    "Views",
                    "RentalCustomerOnboardingWindow.xaml.cs")));
        Assert.Contains(
            "ActualWidth<CompactLayoutWidthThreshold",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnboardingStepSidebar.Visibility=useCompactLayout?Visibility.Collapsed:Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnboardingSidebarColumn.Width=useCompactLayout?newGridLength(0d):newGridLength(250d)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "OnboardingSidebarGapColumn.Width=useCompactLayout?newGridLength(0d):newGridLength(10d)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "Loaded+=(_,_)=>ApplyResponsiveLayout()",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "SizeChanged+=(_,_)=>ApplyResponsiveLayout()",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "ActualHeight<CompactContentHeightThreshold",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "CandidateAssetSummaryPanel.Visibility=useCompactContentLayout?Visibility.Collapsed:Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "TemplateGuidancePanel.Visibility=useCompactContentLayout?Visibility.Collapsed:Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "TemplateSummaryBorder.Visibility=useCompactContentLayout?Visibility.Collapsed:Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "TemplateSummaryGapRow.Height=useCompactContentLayout?newGridLength(0d):newGridLength(10d)",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void RentalContractEditorWindow_KeepsCompactEditorAndPreviewReachable()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var contract = LoadWindow(
            desktopAppDirectory,
            "RentalContractEditorWindow.xaml");

        AssertResponsiveMinimum(contract.Root);
        var contractWindow = Assert.IsType<XElement>(contract.Root);
        Assert.Equal("1180", (string?)contractWindow.Attribute("Width"));
        Assert.Equal("820", (string?)contractWindow.Attribute("Height"));
        Assert.Equal("CanResize", (string?)contractWindow.Attribute("ResizeMode"));
        AssertScrollViewer(
            contract,
            xaml,
            "RentalContractEditorScrollViewer",
            horizontal: "Auto",
            vertical: "Auto");

        var editorScrollViewer = AssertNamedElement(
            contract,
            xaml,
            "RentalContractEditorScrollViewer");
        var editorContent = Assert.Single(editorScrollViewer.Elements());
        Assert.Equal("StackPanel", editorContent.Name.LocalName);
        Assert.Equal("520", (string?)editorContent.Attribute("MinWidth"));

        AssertNamedElementAttribute(
            contract,
            xaml,
            "RentalContractCompactPaneSwitcher",
            "Visibility",
            "Collapsed");
        AssertNamedElementAttribute(
            contract,
            xaml,
            "ShowCompactContractEditorButton",
            "Click",
            "ShowCompactContractEditorButton_Click");
        AssertNamedElementAttribute(
            contract,
            xaml,
            "ShowCompactContractPreviewButton",
            "Click",
            "ShowCompactContractPreviewButton_Click");
        AssertNamedElementAttribute(
            contract,
            xaml,
            "RentalContractEditorColumn",
            "Width",
            "42*");
        AssertNamedElementAttribute(
            contract,
            xaml,
            "RentalContractEditorColumn",
            "MinWidth",
            "560");
        AssertNamedElementAttribute(
            contract,
            xaml,
            "RentalContractSplitterColumn",
            "Width",
            "6");
        AssertNamedElementAttribute(
            contract,
            xaml,
            "RentalContractPreviewColumn",
            "Width",
            "58*");

        var splitter = AssertNamedElement(
            contract,
            xaml,
            "RentalContractWorkspaceSplitter");
        Assert.Equal("GridSplitter", splitter.Name.LocalName);
        Assert.Equal("1", (string?)splitter.Attribute("Grid.Column"));
        Assert.Equal("Columns", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.Equal("True", (string?)splitter.Attribute("Focusable"));

        var previewBorder = AssertNamedElement(
            contract,
            xaml,
            "RentalContractPreviewBorder");
        var documentViewer = Assert.Single(
            previewBorder.Descendants(),
            element => element.Name.LocalName == "DocumentViewer");
        Assert.Equal(
            "{Binding PreviewDocument}",
            (string?)documentViewer.Attribute("Document"));

        var closeButton = AssertNamedElement(
            contract,
            xaml,
            "RentalContractCloseButton");
        Assert.DoesNotContain(
            closeButton.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        Assert.Contains(
            closeButton.Ancestors(),
            ancestor =>
                ancestor.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)ancestor.Attribute("DockPanel.Dock"),
                    "Top",
                    StringComparison.Ordinal));
        var statusFooter = Assert.Single(
            contract.Descendants(),
            element =>
                element.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)element.Attribute("DockPanel.Dock"),
                    "Bottom",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            statusFooter.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");

        var codeBehind = RemoveWhitespace(
            File.ReadAllText(
                Path.Combine(
                    desktopAppDirectory,
                    "Views",
                    "RentalContractEditorWindow.xaml.cs")));
        Assert.Contains(
            "ActualWidth<CompactWorkspaceWidthThreshold",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractCompactPaneSwitcher.Visibility=useCompactLayout?Visibility.Visible:Visibility.Collapsed",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractEditorColumn.Width=_showCompactEditorPane?newGridLength(1d,GridUnitType.Star):newGridLength(0d)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractPreviewColumn.Width=_showCompactEditorPane?newGridLength(0d):newGridLength(1d,GridUnitType.Star)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractEditorScrollViewer.Visibility=_showCompactEditorPane?Visibility.Visible:Visibility.Collapsed",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractPreviewBorder.Visibility=_showCompactEditorPane?Visibility.Collapsed:Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractWorkspaceSplitter.Visibility=Visibility.Collapsed",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractSplitterColumn.Width=newGridLength(0d)",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractEditorScrollViewer.Visibility=Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractPreviewBorder.Visibility=Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractWorkspaceSplitter.Visibility=Visibility.Visible",
            codeBehind,
            StringComparison.Ordinal);
        Assert.Contains(
            "RentalContractSplitterColumn.Width=newGridLength(6d)",
            codeBehind,
            StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryTransferWindow_KeepsCompactWorkspaceAndSectionsReachable()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var transfer = LoadWindow(
            desktopAppDirectory,
            "InventoryTransferWindow.xaml");

        AssertResponsiveMinimum(transfer.Root);
        var transferWindow = Assert.IsType<XElement>(transfer.Root);
        Assert.Equal("1480", (string?)transferWindow.Attribute("Width"));
        Assert.Equal("860", (string?)transferWindow.Attribute("Height"));
        Assert.Equal("CanResize", (string?)transferWindow.Attribute("ResizeMode"));
        AssertDataGridVirtualizationStyle(transfer);

        AssertNamedElementAttribute(
            transfer,
            xaml,
            "InventoryTransferCompactPaneSwitcher",
            "Visibility",
            "Collapsed");
        AssertNamedElementAttribute(
            transfer,
            xaml,
            "InventoryTransferDetailSectionSwitcher",
            "Visibility",
            "Collapsed");

        var headerGrid = AssertNamedElement(
            transfer,
            xaml,
            "InventoryTransferHeaderGrid");
        var headerColumns = headerGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .ToArray();
        Assert.Collection(
            headerColumns,
            titleColumn => Assert.Equal("*", (string?)titleColumn.Attribute("Width")),
            actionsColumn => Assert.Equal("2*", (string?)actionsColumn.Attribute("Width")));
        AssertScrollViewer(
            transfer,
            xaml,
            "InventoryTransferHeaderActionsScrollViewer",
            horizontal: "Auto",
            vertical: "Disabled");
        AssertNamedElementAttribute(
            transfer,
            xaml,
            "InventoryTransferHeaderActionsScrollViewer",
            "HorizontalAlignment",
            "Stretch");

        foreach (var buttonContract in new[]
                 {
                     (Name: "ShowCompactTransferListButton", Content: "최근 문서", Click: "ShowCompactTransferListButton_Click"),
                     (Name: "ShowCompactTransferWorkButton", Content: "작업 내용", Click: "ShowCompactTransferWorkButton_Click"),
                     (Name: "ShowCompactTransferBasicButton", Content: "기본 정보", Click: "ShowCompactTransferBasicButton_Click"),
                     (Name: "ShowCompactTransferEntryButton", Content: "품목 입력·수령", Click: "ShowCompactTransferEntryButton_Click"),
                     (Name: "ShowCompactTransferLinesButton", Content: "품목 목록", Click: "ShowCompactTransferLinesButton_Click")
                 })
        {
            var button = AssertNamedElement(transfer, xaml, buttonContract.Name);
            Assert.Equal("Button", button.Name.LocalName);
            Assert.Equal(buttonContract.Content, (string?)button.Attribute("Content"));
            Assert.Equal(buttonContract.Click, (string?)button.Attribute("Click"));
        }

        AssertScrollViewer(
            transfer,
            xaml,
            "InventoryTransferBasicScrollViewer",
            horizontal: "Auto",
            vertical: "Auto");
        var basicScrollViewer = AssertNamedElement(
            transfer,
            xaml,
            "InventoryTransferBasicScrollViewer");
        var basicContent = Assert.Single(basicScrollViewer.Elements());
        Assert.Equal("StackPanel", basicContent.Name.LocalName);
        Assert.Equal("900", (string?)basicContent.Attribute("MinWidth"));

        AssertScrollViewer(
            transfer,
            xaml,
            "InventoryTransferEntryScrollViewer",
            horizontal: "Auto",
            vertical: "Auto");
        var entryScrollViewer = AssertNamedElement(
            transfer,
            xaml,
            "InventoryTransferEntryScrollViewer");
        var entryContent = Assert.Single(entryScrollViewer.Elements());
        Assert.Equal("Grid", entryContent.Name.LocalName);
        Assert.Equal("1050", (string?)entryContent.Attribute("MinWidth"));

        foreach (var gridContract in new[]
                 {
                     (AutomationId: "TransferListGrid", ItemsSource: "{Binding Transfers}"),
                     (AutomationId: "TransferLinesGrid", ItemsSource: "{Binding Lines}")
                 })
        {
            var dataGrid = Assert.Single(
                transfer.Descendants(),
                element => string.Equals(
                    (string?)element.Attribute("AutomationProperties.AutomationId"),
                    gridContract.AutomationId,
                    StringComparison.Ordinal));
            Assert.Equal("DataGrid", dataGrid.Name.LocalName);
            Assert.Equal(
                gridContract.ItemsSource,
                (string?)dataGrid.Attribute("ItemsSource"));
            Assert.DoesNotContain(
                dataGrid.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }

        var splitter = AssertNamedElement(
            transfer,
            xaml,
            "InventoryTransferWorkspaceSplitter");
        Assert.Equal("GridSplitter", splitter.Name.LocalName);
        Assert.Equal("1", (string?)splitter.Attribute("Grid.Column"));
        Assert.Equal("True", (string?)splitter.Attribute("Focusable"));
        Assert.Equal("Columns", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.Equal("True", (string?)splitter.Attribute("ShowsPreview"));
        Assert.False(string.IsNullOrWhiteSpace((string?)splitter.Attribute("ToolTip")));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)splitter.Attribute("AutomationProperties.Name")));

        foreach (var automationId in new[]
                 {
                     "NewTransferButton",
                     "SaveTransferButton",
                     "DeleteTransferButton"
                 })
        {
            var headerButton = Assert.Single(
                transfer.Descendants(),
                element => string.Equals(
                    (string?)element.Attribute("AutomationProperties.AutomationId"),
                    automationId,
                    StringComparison.Ordinal));
            Assert.Contains(
                headerButton.Ancestors(),
                ancestor =>
                    ancestor.Name.LocalName == "Border" &&
                    string.Equals(
                        (string?)ancestor.Attribute("DockPanel.Dock"),
                        "Top",
                        StringComparison.Ordinal));
            Assert.DoesNotContain(
                headerButton.Ancestors(),
                ancestor =>
                    string.Equals(
                        (string?)ancestor.Attribute(xaml + "Name"),
                        "InventoryTransferBasicScrollViewer",
                        StringComparison.Ordinal) ||
                    string.Equals(
                        (string?)ancestor.Attribute(xaml + "Name"),
                        "InventoryTransferEntryScrollViewer",
                        StringComparison.Ordinal));
        }

        var statusFooter = Assert.Single(
            transfer.Descendants(),
            element => string.Equals(
                (string?)element.Attribute("AutomationProperties.AutomationId"),
                "TransferStatusMessageText",
                StringComparison.Ordinal));
        Assert.Contains(
            statusFooter.Ancestors(),
            ancestor =>
                ancestor.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)ancestor.Attribute("DockPanel.Dock"),
                    "Bottom",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            statusFooter.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");

        var codeBehind = RemoveWhitespace(
            File.ReadAllText(
                Path.Combine(
                    desktopAppDirectory,
                    "Views",
                    "InventoryTransferWindow.xaml.cs")));
        foreach (var expectedCode in new[]
                 {
                     "ActualWidth<CompactWorkspaceWidthThreshold",
                     "ActualHeight<CompactDetailHeightThreshold",
                     "InventoryTransferCompactPaneSwitcher.Visibility=useCompactLayout?Visibility.Visible:Visibility.Collapsed",
                     "InventoryTransferDetailSectionSwitcher.Visibility=useCompactLayout?Visibility.Visible:Visibility.Collapsed",
                     "InventoryTransferListColumn.MinWidth=0d",
                     "InventoryTransferListColumn.Width=_showCompactTransferList?newGridLength(1d,GridUnitType.Star):newGridLength(0d)",
                     "InventoryTransferWorkColumn.Width=_showCompactTransferList?newGridLength(0d):newGridLength(1d,GridUnitType.Star)",
                     "InventoryTransferWorkspaceSplitter.Visibility=Visibility.Collapsed",
                     "InventoryTransferListColumn.MinWidth=360d",
                     "InventoryTransferListColumn.Width=_normalTransferListWidth",
                     "InventoryTransferWorkColumn.Width=_normalTransferWorkWidth",
                     "InventoryTransferSplitterColumn.Width=newGridLength(5d)",
                     "InventoryTransferWorkspaceSplitter.Visibility=Visibility.Visible",
                     "InventoryTransferBasicRow.Height=GridLength.Auto",
                     "InventoryTransferEntryRow.Height=GridLength.Auto",
                     "InventoryTransferLinesRow.Height=newGridLength(1d,GridUnitType.Star)",
                     "InventoryTransferBasicPanel.Visibility=Visibility.Visible",
                     "InventoryTransferEntryPanel.Visibility=Visibility.Visible",
                     "InventoryTransferLinesPanel.Visibility=Visibility.Visible",
                     "InventoryTransferBasicRow.Height=showBasic?newGridLength(1d,GridUnitType.Star):newGridLength(0d)",
                     "InventoryTransferEntryRow.Height=showEntry?newGridLength(1d,GridUnitType.Star):newGridLength(0d)",
                     "InventoryTransferLinesRow.Height=showLines?newGridLength(1d,GridUnitType.Star):newGridLength(0d)",
                     "InventoryTransferBasicPanel.Visibility=showBasic?Visibility.Visible:Visibility.Collapsed",
                     "InventoryTransferEntryPanel.Visibility=showEntry?Visibility.Visible:Visibility.Collapsed",
                     "InventoryTransferLinesPanel.Visibility=showLines?Visibility.Visible:Visibility.Collapsed"
                 })
        {
            Assert.Contains(expectedCode, codeBehind, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void CoreChildWindowConstructors_ApplyPolicyBeforeDataContextSetup()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        var viewDirectory = Path.Combine(desktopAppDirectory, "Views");

        foreach (var fileName in new[]
                 {
                     "CustomerEditWindow.xaml.cs",
                     "InventoryWindow.xaml.cs",
                     "RentalAssetWindow.xaml.cs",
                      "RentalBillingWindow.xaml.cs",
                      "SalesWindow.xaml.cs",
                      "PaymentWindow.xaml.cs",
                      "RentalCustomerOnboardingWindow.xaml.cs",
                      "RentalContractEditorWindow.xaml.cs",
                      "InventoryTransferWindow.xaml.cs",
                      "AttachmentSelectionWindow.xaml.cs",
                      "RentalAssignmentHistoryEditWindow.xaml.cs",
                      "RentalEquipmentReplacementWindow.xaml.cs",
                      "RentalReturnReportInputWindow.xaml.cs"
                  })
        {
            var source = File.ReadAllText(
                Path.Combine(viewDirectory, fileName));
            var initializeIndex = source.IndexOf(
                "InitializeComponent();",
                StringComparison.Ordinal);
            var policyIndex = source.IndexOf(
                "ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);",
                StringComparison.Ordinal);
            var dataContextIndex = source.IndexOf(
                "DataContext =",
                StringComparison.Ordinal);

            Assert.True(initializeIndex >= 0, fileName);
            Assert.True(policyIndex > initializeIndex, fileName);
            if (dataContextIndex >= 0)
                Assert.True(policyIndex < dataContextIndex, fileName);
        }

        foreach (var (fileName, firstUiSetupMarker) in
                 new (string FileName, string FirstUiSetupMarker)[]
                 {
                     ("AttachmentSelectionWindow.xaml.cs", "_viewModel = viewModel;"),
                     ("RentalAssignmentHistoryEditWindow.xaml.cs", "DataContext = EditRequest;"),
                     ("RentalEquipmentReplacementWindow.xaml.cs", "OriginalSummaryText.Text ="),
                     ("RentalReturnReportInputWindow.xaml.cs", "ReturnReasonBox.Text =")
                 })
        {
            var source = File.ReadAllText(
                Path.Combine(viewDirectory, fileName));
            const string policyCall =
                "ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(this);";
            var policyIndex = source.IndexOf(
                policyCall,
                StringComparison.Ordinal);
            var markerIndex = source.IndexOf(
                firstUiSetupMarker,
                StringComparison.Ordinal);

            Assert.True(policyIndex >= 0, fileName);
            Assert.True(markerIndex > policyIndex, fileName);
            Assert.Equal(
                policyIndex,
                source.LastIndexOf(policyCall, StringComparison.Ordinal));
        }

        var salesSource = File.ReadAllText(
            Path.Combine(viewDirectory, "SalesWindow.xaml.cs"));
        Assert.Contains(
            "CompactWorkspaceHeightThreshold = 620d",
            salesSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SizeChanged += (_, _) => ApplyResponsiveWorkspaceLayout();",
            salesSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "!ResponsiveWindowBehavior.GetIsEnabled(this)",
            salesSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SalesCompactSectionSwitcher.Visibility = useCompactLayout",
            salesSource,
            StringComparison.Ordinal);

        var paymentSource = File.ReadAllText(
            Path.Combine(viewDirectory, "PaymentWindow.xaml.cs"));
        Assert.Contains(
            "CompactWorkspaceHeightThreshold = 620d",
            paymentSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SizeChanged += (_, _) => ApplyResponsiveWorkspaceLayout();",
            paymentSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "!ResponsiveWindowBehavior.GetIsEnabled(this)",
            paymentSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PaymentCompactSectionSwitcher.Visibility = useCompactLayout",
            paymentSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "PaymentCommandWorkspaceSplitter.Visibility = Visibility.Collapsed",
            paymentSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SalesDocumentHeaderScrollViewer.Visibility =",
            salesSource,
            StringComparison.Ordinal);
        Assert.Contains(
            "SalesLineEntryScrollViewer.Visibility =",
            salesSource,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnvironmentSettingsSyncTab_ExposesEverySectionThroughDefaultSizeOverflowNavigation()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        var document = LoadWindow(
            desktopAppDirectory,
            "EnvironmentSettingsWindow.xaml");
        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

        var syncTab = AssertNamedElement(
            document,
            xamlNamespace,
            "SyncTab");
        var scrollViewer = Assert.Single(
            syncTab.Elements(),
            element => element.Name.LocalName == "ScrollViewer");

        Assert.Equal(
            "SyncTabScrollViewer",
            (string?)scrollViewer.Attribute(xamlNamespace + "Name"));
        Assert.Equal(
            "Disabled",
            (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal(
            "Auto",
            (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal(
            "False",
            (string?)scrollViewer.Attribute("CanContentScroll"));

        var contentGrid = Assert.Single(
            scrollViewer.Elements(),
            element => element.Name.LocalName == "Grid");
        Assert.Equal(
            "SyncTabContentGrid",
            (string?)contentGrid.Attribute(xamlNamespace + "Name"));
        Assert.Equal("900", (string?)contentGrid.Attribute("MinWidth"));

        var rowDefinitions = Assert.Single(
                contentGrid.Elements(),
                element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "Auto", "Auto", "300" }, rowDefinitions);

        var actionHeader = Assert.Single(
            contentGrid.Elements(),
            element =>
                element.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "0",
                    StringComparison.Ordinal));
        var actionPanel = Assert.Single(
            actionHeader.Descendants(),
            element =>
                element.Name.LocalName == "WrapPanel" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "1",
                    StringComparison.Ordinal));
        Assert.Equal("0,12,0,0", (string?)actionPanel.Attribute("Margin"));

        var lowerStatusTableBindings = contentGrid
            .Descendants()
            .Where(element => element.Name.LocalName == "DataGrid")
            .Select(element => (string?)element.Attribute("ItemsSource"))
            .Where(value => value is not null)
            .ToArray();
        Assert.Contains("{Binding StoredSyncCredentials}", lowerStatusTableBindings);
        Assert.Contains("{Binding SyncScopeStatuses}", lowerStatusTableBindings);
    }

    [Fact]
    public void SyncDiagnosticsWindow_KeepsHeaderExplanationAndActionsReachableAtMinimumWidth()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        var document = LoadWindow(
            desktopAppDirectory,
            "SyncDiagnosticsWindow.xaml");

        var rootGrid = Assert.Single(
            document.Root!.Elements(),
            element => element.Name.LocalName == "Grid");
        var header = Assert.Single(
            rootGrid.Elements(),
            element =>
                element.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "0",
                    StringComparison.Ordinal));
        var headerGrid = Assert.Single(
            header.Elements(),
            element => element.Name.LocalName == "Grid");
        var rowDefinitions = Assert.Single(
                headerGrid.Elements(),
                element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "Auto" }, rowDefinitions);

        var explanationPanel = Assert.Single(
            headerGrid.Elements(),
            element =>
                element.Name.LocalName == "StackPanel" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "0",
                    StringComparison.Ordinal));
        var explanation = Assert.Single(
            explanationPanel.Elements(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                ((string?)element.Attribute("Text"))?.Contains(
                    "sync outbox 재시도 상태",
                    StringComparison.Ordinal) == true);
        Assert.Equal("Wrap", (string?)explanation.Attribute("TextWrapping"));

        var actionPanel = Assert.Single(
            headerGrid.Elements(),
            element =>
                element.Name.LocalName == "WrapPanel" &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "1",
                    StringComparison.Ordinal));
        Assert.Equal("0,12,0,0", (string?)actionPanel.Attribute("Margin"));
        Assert.Equal(
            new[]
            {
                "새로고침(F5)",
                "동기화 재시도",
                "공유 캐시 다시 만들기",
                "선택 항목 복구",
                "복구 가능 항목 전체 처리",
                "닫기(F12)"
            },
            actionPanel
                .Elements()
                .Where(element => element.Name.LocalName == "Button")
                .Select(element => (string?)element.Attribute("Content"))
                .ToArray());
    }

    [Fact]
    public void AllProductionWindows_UseGlobalResponsiveSizingAndExposeOverflowNavigation()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        var appXaml = File.ReadAllText(Path.Combine(desktopAppDirectory, "App.xaml"));
        var behaviorCode = File.ReadAllText(Path.Combine(
            desktopAppDirectory,
            "Infrastructure",
            "ResponsiveWindowBehavior.cs"));

        Assert.Contains(
            "<Setter Property=\"infra:ResponsiveWindowBehavior.IsEnabled\" Value=\"True\"/>",
            appXaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "window is global::거래플랜.Desktop.App.MainWindow",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ChildWindowResponsiveLayoutPolicy.ApplyInitialWindowSize(window);",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "EnsureOverflowNavigation(window);",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "HorizontalScrollBarVisibility = ScrollBarVisibility.Auto",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "VerticalScrollBarVisibility = ScrollBarVisibility.Auto",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "content.LayoutUpdated += (_, _) => RefreshContentHostSize();",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ChildWindowResponsiveLayoutPolicy.MinimumContentWidthDip - chromeWidth",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "ChildWindowResponsiveLayoutPolicy.MinimumContentHeightDip - chromeHeight",
            behaviorCode,
            StringComparison.Ordinal);
        var childPolicyCode = File.ReadAllText(Path.Combine(
            desktopAppDirectory,
            "Infrastructure",
            "ChildWindowResponsiveLayoutPolicy.cs"));
        Assert.Contains("MinimumContentWidthDip = 760d", childPolicyCode, StringComparison.Ordinal);
        Assert.Contains("MinimumContentHeightDip = 560d", childPolicyCode, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "content.DesiredSize.Width",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "content.DesiredSize.Height",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ResolveDesignDimension(",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "MeasureDescendantExtent(content)",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsNestedOverflowNavigationBoundary(descendant)",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "element is ScrollViewer or DataGrid or ListBox or ListView or TreeView",
            behaviorCode,
            StringComparison.Ordinal);
        Assert.Contains("window.Initialized += OnWindowInitialized;", behaviorCode, StringComparison.Ordinal);

        var appCode = File.ReadAllText(Path.Combine(desktopAppDirectory, "App.xaml.cs"));
        Assert.Contains("EventManager.RegisterClassHandler(", appCode, StringComparison.Ordinal);
        Assert.Contains("typeof(Window)", appCode, StringComparison.Ordinal);
        Assert.Contains("FrameworkElement.LoadedEvent", appCode, StringComparison.Ordinal);
        Assert.Contains("ResponsiveWindowBehavior.SetIsEnabled(window, true);", appCode, StringComparison.Ordinal);
        Assert.Contains("FullTextLayoutBehavior.SetIsEnabled(window, true);", appCode, StringComparison.Ordinal);
        Assert.Contains("var popupScrollViewer = new ScrollViewer", appCode, StringComparison.Ordinal);
        Assert.Contains("Content = popupScrollViewer", appCode, StringComparison.Ordinal);
        Assert.Contains("VerticalScrollBarVisibility = ScrollBarVisibility.Auto", appCode, StringComparison.Ordinal);
        Assert.Contains("ResponsiveWindowBehavior.SetIsEnabled(popup, false);", appCode, StringComparison.Ordinal);

        var rentalSettings = LoadWindow(desktopAppDirectory, "RentalSettingsWindow.xaml");
        var normalizeCustomerLinksButton = Assert.Single(
            rentalSettings.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Content"),
                    "메인 거래처명 동기화",
                    StringComparison.Ordinal));
        var rentalCleanupHeader = Assert.IsType<XElement>(normalizeCustomerLinksButton.Parent?.Parent);
        Assert.Equal("Grid", rentalCleanupHeader.Name.LocalName);
        Assert.Equal(
            new[] { "*", "Auto" },
            rentalCleanupHeader
                .Elements()
                .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements()
                .Select(element => (string?)element.Attribute("Width"))
                .ToArray());

        var environmentSettings = LoadWindow(desktopAppDirectory, "EnvironmentSettingsWindow.xaml");
        var recycleBinDetailTitle = Assert.Single(
            environmentSettings.Descendants(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "복원 / 영구삭제 상세",
                    StringComparison.Ordinal));
        var recycleBinDetailGrid = recycleBinDetailTitle
            .Ancestors()
            .First(element =>
                element.Name.LocalName == "Grid" &&
                element.Elements().Any(child =>
                    child.Name.LocalName == "Grid.ColumnDefinitions"));
        Assert.Equal(
            new[] { "*", "12", "*" },
            recycleBinDetailGrid
                .Elements()
                .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
                .Elements()
                .Select(element => (string?)element.Attribute("Width"))
                .ToArray());

        var mainWindow = XDocument.Load(Path.Combine(desktopAppDirectory, "MainWindow.xaml"));
        var mainPrintButton = Assert.Single(
            mainWindow.Descendants(),
            element =>
                element.Name.LocalName == "Button" &&
                string.Equals(
                    (string?)element.Attribute("Content"),
                    "전표 인쇄[F9]",
                    StringComparison.Ordinal));
        var mainToolbarGrid = Assert.IsType<XElement>(mainPrintButton.Parent);
        var mainToolbarColumns = mainToolbarGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .ToArray();
        Assert.Equal("128", (string?)mainToolbarColumns[1].Attribute("Width"));

        var tradePrint = LoadWindow(desktopAppDirectory, "TradePrintWindow.xaml");
        var copyCountButtons = tradePrint
            .Descendants()
            .Where(element =>
                element.Name.LocalName == "Button" &&
                ((string?)element.Attribute("Content") is "▲" or "▼"))
            .ToArray();
        Assert.Equal(2, copyCountButtons.Length);
        var copyCountButtonPanel = Assert.IsType<XElement>(copyCountButtons[0].Parent);
        Assert.Same(copyCountButtonPanel, copyCountButtons[1].Parent);
        Assert.Equal("Horizontal", (string?)copyCountButtonPanel.Attribute("Orientation"));
        Assert.All(
            copyCountButtons,
            button =>
            {
                Assert.Equal("24", (string?)button.Attribute("Height"));
                Assert.False(string.IsNullOrWhiteSpace(
                    (string?)button.Attribute("AutomationProperties.Name")));
            });

        XNamespace xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        var syncDiagnostics = LoadWindow(desktopAppDirectory, "SyncDiagnosticsWindow.xaml");
        var syncEventsDataGrid = Assert.Single(
            syncDiagnostics.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xamlNamespace + "Name"),
                "SyncEventsDataGrid",
                StringComparison.Ordinal));
        Assert.Equal("DataGrid", syncEventsDataGrid.Name.LocalName);
        Assert.Equal("180", (string?)syncEventsDataGrid.Attribute("MinHeight"));

        var viewDirectory = Path.Combine(desktopAppDirectory, "Views");
        var windows = Directory
            .EnumerateFiles(viewDirectory, "*.xaml", SearchOption.TopDirectoryOnly)
            .Select(path => (Path: path, Document: XDocument.Load(path)))
            .Where(item => item.Document.Root?.Name.LocalName == "Window")
            .OrderBy(item => item.Path, StringComparer.Ordinal)
            .ToArray();

        Assert.True(windows.Length >= 33, $"Production window count: {windows.Length}");

        foreach (var (path, document) in windows)
        {
            var fileName = Path.GetFileName(path);
            var codeBehindPath = path + ".cs";
            Assert.True(File.Exists(codeBehindPath), fileName);

            var root = Assert.IsType<XElement>(document.Root);
            var directContent = Assert.Single(
                root.Elements(),
                element => !element.Name.LocalName.Contains('.', StringComparison.Ordinal));
            Assert.NotEqual("Canvas", directContent.Name.LocalName);

            var hasOverflowNavigation = document
                .Descendants()
                .Any(element => element.Name.LocalName is
                    "ScrollViewer" or
                    "DataGrid" or
                    "DocumentViewer" or
                    "ListBox" or
                    "ListView" or
                    "TreeView");
            if (string.Equals(fileName, "TradePrintWindow.xaml", StringComparison.Ordinal))
                Assert.False(hasOverflowNavigation, "인쇄창 본문에는 메인 스크롤 컨테이너가 없어야 합니다.");
            else
                Assert.True(hasOverflowNavigation, fileName);

            var preferredWidth = ReadPositiveDimension(root, "Width", 640d);
            var preferredHeight = ReadPositiveDimension(
                root,
                "Height",
                ReadPositiveDimension(root, "MinHeight", 400d));
            var minimumWidth = ReadPositiveDimension(
                root,
                "MinWidth",
                Math.Min(preferredWidth, ChildWindowResponsiveLayoutPolicy.MinimumWidthDip));
            var minimumHeight = ReadPositiveDimension(
                root,
                "MinHeight",
                Math.Min(preferredHeight, ChildWindowResponsiveLayoutPolicy.MinimumHeightDip));

            foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
            {
                var bounds = ChildWindowResponsiveLayoutPolicy.ResolvePhysicalWindowBounds(
                    new Rect(0d, 0d, 1366d, 728d),
                    scale,
                    new Size(preferredWidth, preferredHeight),
                    new Size(minimumWidth, minimumHeight));

                Assert.False(bounds.IsEmpty, $"{fileName} at {scale:P0}");
                Assert.InRange(bounds.Width, 1d, 1366d);
                Assert.InRange(bounds.Height, 1d, 728d);
                Assert.InRange(bounds.Left, 0d, 1366d - bounds.Width + 0.01d);
                Assert.InRange(bounds.Top, 0d, 728d - bounds.Height + 0.01d);
            }
        }

        var customerManagement = LoadWindow(
            desktopAppDirectory,
            "CustomerManagementWindow.xaml");
        var customerManagementFilterPanel = AssertNamedElement(
            customerManagement,
            xamlNamespace,
            "CustomerManagementFilterPanel");
        Assert.Equal("Grid", customerManagementFilterPanel.Name.LocalName);
        Assert.DoesNotContain(
            customerManagementFilterPanel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");

        var yeonsuDelivery = LoadWindow(
            desktopAppDirectory,
            "YeonsuDeliveryWindow.xaml");
        var yeonsuSummaryPanel = AssertNamedElement(
            yeonsuDelivery,
            xamlNamespace,
            "YeonsuSummaryPanel");
        Assert.Equal("UniformGrid", yeonsuSummaryPanel.Name.LocalName);
        Assert.DoesNotContain(
            yeonsuSummaryPanel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        var yeonsuFilterPanel = AssertNamedElement(
            yeonsuDelivery,
            xamlNamespace,
            "YeonsuFilterPanel");
        Assert.Equal("WrapPanel", yeonsuFilterPanel.Name.LocalName);
        Assert.DoesNotContain(
            yeonsuFilterPanel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
    }

    [Fact]
    public void ContentSizedActivityPopup_OptsOutOfGlobalResponsiveMinimums()
    {
        var desktopAppDirectory = FindDesktopAppDirectory();
        var appCode = File.ReadAllText(Path.Combine(desktopAppDirectory, "App.xaml.cs"));
        var responsiveBehaviorCode = File.ReadAllText(
            Path.Combine(
                desktopAppDirectory,
                "Infrastructure",
                "ResponsiveWindowBehavior.cs"));

        Assert.Contains(
            "ResponsiveWindowBehavior.GetIsGlobalLayoutExcluded(window)",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "PreserveContentSizedActivityPopup(popup);",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "popup.Loaded += (_, _) => PreserveContentSizedActivityPopup(popup);",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "SizeToContent = SizeToContent.WidthAndHeight",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Padding = new Thickness(20, 14, 20, 12)",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "Style = new Style(typeof(Window))",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "popup.MinHeight = 0;",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "popup.ClearValue(FrameworkElement.HeightProperty);",
            appCode,
            StringComparison.Ordinal);
        Assert.Contains(
            "IsGlobalLayoutExcludedProperty",
            responsiveBehaviorCode,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Height = 400",
            appCode,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsiveWindowBehavior_AppliesToCompactWindowWithoutEnlargingIt()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var window = new Window
                {
                    Width = 440d,
                    Height = 190d,
                    MinWidth = 360d,
                    MinHeight = 160d,
                    SizeToContent = SizeToContent.Manual
                };
                ResponsiveWindowBehavior.SetIsEnabled(window, true);

                Assert.Equal(SizeToContent.Manual, window.SizeToContent);
                Assert.Equal(440d, window.Width);
                Assert.Equal(190d, window.Height);
                Assert.InRange(window.MinWidth, 1d, 440d);
                Assert.InRange(window.MinHeight, 1d, 190d);
                window.Close();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "STA responsive window test timed out.");
        Assert.Null(failure);
    }

    [Fact]
    public void Policy_PrefersOwnerMonitorAndDoesNotSetPermanentMaximums()
    {
        var policyPath = Path.Combine(
            FindDesktopAppDirectory(),
            "Infrastructure",
            "ChildWindowResponsiveLayoutPolicy.cs");
        var source = File.ReadAllText(policyPath);

        Assert.Contains("window.Owner is not null", source, StringComparison.Ordinal);
        Assert.Contains("MonitorFromWindow(\n                    ownerHandle", source, StringComparison.Ordinal);
        Assert.Contains("window.SourceInitialized += sourceInitializedHandler;", source, StringComparison.Ordinal);
        Assert.Contains("WindowDpiChangedMessage", source, StringComparison.Ordinal);
        Assert.Contains("HwndSourceHook", source, StringComparison.Ordinal);
        Assert.Contains("window.LocationChanged += locationChangedHandler;", source, StringComparison.Ordinal);
        Assert.Contains("QueuePlacementOnCurrentMonitor", source, StringComparison.Ordinal);
        Assert.Contains("GetDpiForMonitor(", source, StringComparison.Ordinal);
        Assert.Contains("SetWindowPos(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("window.MaxWidth", source, StringComparison.Ordinal);
        Assert.DoesNotContain("window.MaxHeight", source, StringComparison.Ordinal);
    }

    private static void AssertCommandAndWorkspaceRows(XDocument document)
    {
        var window = Assert.IsType<XElement>(document.Root);
        var rootGrid = Assert.Single(
            window.Elements(),
            element => element.Name.LocalName == "Grid");
        var rowDefinitions = rootGrid
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();

        Assert.Collection(
            rowDefinitions,
            commandRow =>
            {
                Assert.Equal("Auto", (string?)commandRow.Attribute("Height"));
                Assert.Null((string?)commandRow.Attribute("MinHeight"));
                Assert.Null((string?)commandRow.Attribute("MaxHeight"));
            },
            workspaceRow =>
            {
                Assert.Equal("3*", (string?)workspaceRow.Attribute("Height"));
                Assert.Equal("140", (string?)workspaceRow.Attribute("MinHeight"));
            },
            statusRow =>
                Assert.Equal("Auto", (string?)statusRow.Attribute("Height")));
    }

    private static double ReadPositiveDimension(
        XElement element,
        string attributeName,
        double fallback)
    {
        var value = (string?)element.Attribute(attributeName);
        return double.TryParse(
                   value,
                   System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture,
                   out var parsed) &&
               double.IsFinite(parsed) &&
               parsed > 0d
            ? parsed
            : fallback;
    }

    private static void AssertInventoryDetailWorkspaceRows(
        XDocument document,
        XNamespace xaml)
    {
        var workspace = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Grid" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    "InventoryDetailWorkspaceGrid",
                    StringComparison.Ordinal));
        var rowDefinitions = workspace
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();

        Assert.Collection(
            rowDefinitions,
            summaryRow =>
                Assert.Equal("Auto", (string?)summaryRow.Attribute("Height")),
            detailRow =>
            {
                Assert.Equal("3*", (string?)detailRow.Attribute("Height"));
                Assert.Equal("56", (string?)detailRow.Attribute("MinHeight"));
            },
            splitterRow =>
                Assert.Equal("Auto", (string?)splitterRow.Attribute("Height")),
            historyRow =>
            {
                Assert.Equal("*", (string?)historyRow.Attribute("Height"));
                Assert.Equal("64", (string?)historyRow.Attribute("MinHeight"));
                Assert.Equal("156", (string?)historyRow.Attribute("MaxHeight"));
            });

        var detailScrollViewer = Assert.Single(
            workspace.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "InventoryDetailScrollViewer",
                StringComparison.Ordinal));
        Assert.Equal("1", (string?)detailScrollViewer.Attribute("Grid.Row"));

        var historySplitter = Assert.Single(
            workspace.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "InventoryTransferHistorySplitter",
                StringComparison.Ordinal));
        Assert.Equal("GridSplitter", historySplitter.Name.LocalName);
        Assert.Equal("2", (string?)historySplitter.Attribute("Grid.Row"));
        Assert.Equal("6", (string?)historySplitter.Attribute("Height"));
        Assert.Equal(
            "Stretch",
            (string?)historySplitter.Attribute("HorizontalAlignment"));
        Assert.Equal("Rows", (string?)historySplitter.Attribute("ResizeDirection"));
        Assert.Equal(
            "PreviousAndNext",
            (string?)historySplitter.Attribute("ResizeBehavior"));
        Assert.Equal("True", (string?)historySplitter.Attribute("Focusable"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)historySplitter.Attribute("AutomationProperties.Name")));

        var historyPanel = Assert.Single(
            workspace.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "InventoryTransferHistoryPanel",
                StringComparison.Ordinal));
        Assert.Equal("3", (string?)historyPanel.Attribute("Grid.Row"));
        Assert.Equal("8,2,8,4", (string?)historyPanel.Attribute("Margin"));
        Assert.Equal("4", (string?)historyPanel.Attribute("Padding"));

        foreach (var dataGridName in new[]
                 {
                     "ItemsDataGrid",
                     "InventoryTransferHistoryDataGrid",
                 })
        {
            var dataGrid = Assert.Single(
                document.Descendants(),
                element => string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    dataGridName,
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                dataGrid.Ancestors(),
                ancestor => ReferenceEquals(ancestor, detailScrollViewer));
            Assert.DoesNotContain(
                dataGrid.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
        }

        var historyDataGrid = Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "InventoryTransferHistoryDataGrid",
                StringComparison.Ordinal));
        Assert.Equal("NaN", (string?)historyDataGrid.Attribute("ColumnHeaderHeight"));
        Assert.Equal("NaN", (string?)historyDataGrid.Attribute("RowHeight"));
        Assert.Equal("32", (string?)historyDataGrid.Attribute("MinRowHeight"));
    }

    private static void AssertSalesWorkspaceRows(
        XDocument document,
        XNamespace xaml)
    {
        var workspace = Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "SalesWorkspaceGrid",
                StringComparison.Ordinal));
        var rowDefinitions = workspace
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();

        Assert.Collection(
            rowDefinitions,
            customerHeaderRow =>
            {
                Assert.Equal("Auto", (string?)customerHeaderRow.Attribute("Height"));
                Assert.Null(customerHeaderRow.Attribute("MinHeight"));
                Assert.Null(customerHeaderRow.Attribute("MaxHeight"));
            },
            salesLinesRow =>
            {
                Assert.Equal("4*", (string?)salesLinesRow.Attribute("Height"));
                Assert.Equal("152", (string?)salesLinesRow.Attribute("MinHeight"));
            },
            splitterRow =>
                Assert.Equal("Auto", (string?)splitterRow.Attribute("Height")),
            itemSearchRow =>
            {
                Assert.Equal("1*", (string?)itemSearchRow.Attribute("Height"));
                Assert.Equal("176", (string?)itemSearchRow.Attribute("MinHeight"));
                Assert.Null(itemSearchRow.Attribute("MaxHeight"));
            });

        var dataGridCellStyle = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                string.Equals(
                    (string?)element.Attribute("TargetType"),
                    "DataGridCell",
                    StringComparison.Ordinal));
        var dataGridCellPadding = Assert.Single(
            dataGridCellStyle.Elements(),
            element =>
                element.Name.LocalName == "Setter" &&
                string.Equals(
                    (string?)element.Attribute("Property"),
                    "Padding",
                    StringComparison.Ordinal));
        Assert.Equal("4,0", (string?)dataGridCellPadding.Attribute("Value"));

        var splitter = Assert.Single(
            workspace.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "SalesGridHeightSplitter",
                StringComparison.Ordinal));
        Assert.Equal("GridSplitter", splitter.Name.LocalName);
        Assert.Equal("2", (string?)splitter.Attribute("Grid.Row"));
        Assert.Equal("6", (string?)splitter.Attribute("Height"));
        Assert.Equal("Stretch", (string?)splitter.Attribute("HorizontalAlignment"));
        Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.Equal("True", (string?)splitter.Attribute("Focusable"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)splitter.Attribute("AutomationProperties.Name")));

        foreach (var dataGridName in new[]
                 {
                     "SalesLinesDataGrid",
                     "ItemSearchResultsDataGrid",
                 })
        {
            var dataGrid = Assert.Single(
                document.Descendants(),
                element => string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    dataGridName,
                    StringComparison.Ordinal));
            Assert.DoesNotContain(
                dataGrid.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");
            Assert.Equal("NaN", (string?)dataGrid.Attribute("ColumnHeaderHeight"));
            Assert.Equal("NaN", (string?)dataGrid.Attribute("RowHeight"));
            Assert.Null(dataGrid.Attribute("MinRowHeight"));
            Assert.Equal(
                "{StaticResource MainTransactionDataRowStyle}",
                (string?)dataGrid.Attribute("RowStyle"));
        }

        var mainWorkspace = AssertNamedElement(
            document,
            xaml,
            "SalesMainWorkspaceGrid");
        Assert.Equal("1", (string?)mainWorkspace.Attribute("Grid.Row"));
        Assert.Null(mainWorkspace.Attribute("Grid.RowSpan"));

        var itemSearchWorkspace = AssertNamedElement(
            document,
            xaml,
            "SalesItemSearchWorkspace");

        var printOptionsBorder = AssertNamedElement(
            document,
            xaml,
            "SalesPrintOptionsBorder");
        Assert.Equal("0", (string?)printOptionsBorder.Attribute("Grid.Row"));
        Assert.Equal("2", (string?)printOptionsBorder.Attribute("Grid.RowSpan"));
        Assert.Equal("10,6", (string?)printOptionsBorder.Attribute("Padding"));
        var printOptionsPanel = AssertNamedElement(
            document,
            xaml,
            "SalesPrintOptionsPanel");
        Assert.Equal("Grid", printOptionsPanel.Name.LocalName);
        var printOptionColumns = Assert.Single(
                printOptionsPanel.Elements(),
                element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .Select(element => (string?)element.Attribute("Width"))
            .ToArray();
        Assert.Equal(new[] { "*", "*" }, printOptionColumns);
        var purchaseReceivingOptions = AssertNamedElement(
            document,
            xaml,
            "SalesPurchaseReceivingOptionsGrid");
        Assert.Equal("Grid", purchaseReceivingOptions.Name.LocalName);
        var purchaseReceivingColumns = Assert.Single(
                purchaseReceivingOptions.Elements(),
                element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "ColumnDefinition")
            .Select(element => (string?)element.Attribute("Width"))
            .ToArray();
        Assert.Equal(new[] { "*", "*" }, purchaseReceivingColumns);
        Assert.DoesNotContain(
            purchaseReceivingOptions.Elements(),
            element => element.Name.LocalName == "StackPanel");
        Assert.DoesNotContain(
            printOptionsPanel.Ancestors(),
            ancestor => ancestor.Name.LocalName == "ScrollViewer");
        foreach (var requiredOption in new[]
                 {
                     "거래명세서",
                     "견적서",
                     "대금청구서",
                     "날짜 인쇄함",
                     "단가 인쇄함",
                     "인쇄하기[F9]",
                     "출력물 편집",
                     "세금계산서 발행 완료",
                     "세금계산서 인쇄",
                 })
        {
            Assert.Contains(
                printOptionsPanel.Descendants(),
                element =>
                    string.Equals(
                        (string?)element.Attribute("Content"),
                        requiredOption,
                        StringComparison.Ordinal));
        }
        Assert.Equal("3", (string?)itemSearchWorkspace.Attribute("Grid.Row"));
        Assert.Contains(
            itemSearchWorkspace.Descendants(),
            element =>
                element.Name.LocalName == "TextBlock" &&
                string.Equals(
                    (string?)element.Attribute("Text"),
                    "품목 검색 목록",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            itemSearchWorkspace.Descendants(),
            element => string.Equals(
                (string?)element.Attribute("Text"),
                "[전표 입력 가능 품목 - 재고/비재고 청구항목]",
                StringComparison.Ordinal));
    }

    private static void AssertPaymentBodyRows(
        XDocument document,
        XNamespace xaml)
    {
        var body = AssertNamedElement(document, xaml, "PaymentBodyGrid");
        var rowDefinitions = body
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();

        Assert.Collection(
            rowDefinitions,
            switcherRow =>
                Assert.Equal("Auto", (string?)switcherRow.Attribute("Height")),
            commandRow =>
            {
                Assert.Equal("3*", (string?)commandRow.Attribute("Height"));
                Assert.Equal("100", (string?)commandRow.Attribute("MinHeight"));
                Assert.Equal("440", (string?)commandRow.Attribute("MaxHeight"));
            },
            splitterRow =>
                Assert.Equal("Auto", (string?)splitterRow.Attribute("Height")),
            workspaceRow =>
            {
                Assert.Equal("2*", (string?)workspaceRow.Attribute("Height"));
                Assert.Equal("112", (string?)workspaceRow.Attribute("MinHeight"));
            });

        var splitter = AssertNamedElement(
            document,
            xaml,
            "PaymentCommandWorkspaceSplitter");
        Assert.Equal("GridSplitter", splitter.Name.LocalName);
        Assert.Equal("2", (string?)splitter.Attribute("Grid.Row"));
        Assert.Equal("6", (string?)splitter.Attribute("Height"));
        Assert.Equal("Stretch", (string?)splitter.Attribute("HorizontalAlignment"));
        Assert.Equal("Rows", (string?)splitter.Attribute("ResizeDirection"));
        Assert.Equal("PreviousAndNext", (string?)splitter.Attribute("ResizeBehavior"));
        Assert.Equal("True", (string?)splitter.Attribute("ShowsPreview"));
        Assert.Equal("True", (string?)splitter.Attribute("Focusable"));
        Assert.Equal("SizeNS", (string?)splitter.Attribute("Cursor"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)splitter.Attribute("AutomationProperties.Name")));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)splitter.Attribute("ToolTip")));
    }

    private static void AssertPaymentFixedHeaderFooter(
        XDocument document,
        XNamespace xaml)
    {
        var window = Assert.IsType<XElement>(document.Root);
        var rootGrid = Assert.Single(
            window.Elements(),
            element => element.Name.LocalName == "Grid");
        var rowDefinitions = rootGrid
            .Elements()
            .Single(element =>
                element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .ToArray();
        Assert.Collection(
            rowDefinitions,
            headerRow =>
                Assert.Equal("Auto", (string?)headerRow.Attribute("Height")),
            bodyRow =>
                Assert.Equal("*", (string?)bodyRow.Attribute("Height")),
            footerRow =>
                Assert.Equal("Auto", (string?)footerRow.Attribute("Height")));

        var closeButton = AssertNamedElement(
            document,
            xaml,
            "PaymentCloseButton");
        var closeBorder = Assert.Single(
            closeButton.Ancestors(),
            ancestor =>
                ancestor.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)ancestor.Attribute("Grid.Row"),
                    "0",
                    StringComparison.Ordinal));
        Assert.Same(rootGrid, closeBorder.Parent);

        var saveButton = AssertNamedElement(
            document,
            xaml,
            "PaymentSaveButton");
        var saveBorder = Assert.Single(
            saveButton.Ancestors(),
            ancestor =>
                ancestor.Name.LocalName == "Border" &&
                string.Equals(
                    (string?)ancestor.Attribute("Grid.Row"),
                    "2",
                    StringComparison.Ordinal));
        Assert.Same(rootGrid, saveBorder.Parent);
    }

    private static void AssertPaymentWorkspaceTabs(
        XDocument document,
        XNamespace xaml)
    {
        var tabs = AssertNamedElement(document, xaml, "PaymentWorkspaceTabs");
        Assert.Equal("TabControl", tabs.Name.LocalName);
        Assert.Equal("3", (string?)tabs.Attribute("Grid.Row"));

        var tabItems = tabs
            .Elements()
            .Where(element => element.Name.LocalName == "TabItem")
            .ToArray();
        Assert.Collection(
            tabItems,
            historyTab =>
                Assert.Equal("최근 처리내역", (string?)historyTab.Attribute("Header")),
            attachmentTab =>
                Assert.Equal("증빙 관리", (string?)attachmentTab.Attribute("Header")));

        AssertNamedElementType(
            document,
            xaml,
            "PaymentHistoryActions",
            "WrapPanel");

        foreach (var dataGridName in new[]
                 {
                     "PaymentHistoryDataGrid",
                     "PaymentAttachmentDataGrid",
                 })
        {
            var dataGrid = AssertNamedElement(document, xaml, dataGridName);
            Assert.Equal("DataGrid", dataGrid.Name.LocalName);
            Assert.Null(dataGrid.Attribute("MinHeight"));
            Assert.DoesNotContain(
                dataGrid.Ancestors(),
                ancestor => ancestor.Name.LocalName == "ScrollViewer");

            var parentGrid = Assert.IsType<XElement>(dataGrid.Parent);
            Assert.Equal("Grid", parentGrid.Name.LocalName);
            var rowDefinitions = parentGrid
                .Elements()
                .Single(element =>
                    element.Name.LocalName == "Grid.RowDefinitions")
                .Elements()
                .ToArray();
            var gridRow = int.Parse(
                (string?)dataGrid.Attribute("Grid.Row") ?? "0");
            Assert.Equal(
                "*",
                (string?)rowDefinitions[gridRow].Attribute("Height"));
        }

        var historyDataGrid = AssertNamedElement(
            document,
            xaml,
            "PaymentHistoryDataGrid");
        Assert.Equal(
            "HistoryDataGrid_MouseDoubleClick",
            (string?)historyDataGrid.Attribute("MouseDoubleClick"));
        Assert.Equal(
            "{Binding History}",
            (string?)historyDataGrid.Attribute("ItemsSource"));

        var attachmentDataGrid = AssertNamedElement(
            document,
            xaml,
            "PaymentAttachmentDataGrid");
        Assert.Equal(
            "{Binding Attachments}",
            (string?)attachmentDataGrid.Attribute("ItemsSource"));
        Assert.Contains(
            tabItems[1].Descendants(),
            element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding AddAttachmentCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            tabItems[1].Descendants(),
            element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding PreviewAttachmentCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            tabItems[1].Descendants(),
            element => string.Equals(
                (string?)element.Attribute("Command"),
                "{Binding DeleteAttachmentCommand}",
                StringComparison.Ordinal));
    }

    private static void AssertPaymentCompactSectionSwitcher(
        XDocument document,
        XNamespace xaml)
    {
        var switcher = AssertNamedElement(
            document,
            xaml,
            "PaymentCompactSectionSwitcher");
        Assert.Equal("Collapsed", (string?)switcher.Attribute("Visibility"));
        Assert.Equal("0", (string?)switcher.Attribute("Grid.Row"));

        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactPaymentCommandButton",
            "Click",
            "ShowCompactPaymentCommandButton_Click");
        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactPaymentCommandButton",
            "Width",
            "82");
        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactPaymentWorkspaceButton",
            "Click",
            "ShowCompactPaymentWorkspaceButton_Click");
        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactPaymentWorkspaceButton",
            "Width",
            "82");
    }

    private static void AssertSalesCompactSectionSwitcher(
        XDocument document,
        XNamespace xaml)
    {
        var switcher = AssertNamedElement(
            document,
            xaml,
            "SalesCompactSectionSwitcher");
        Assert.Equal("Collapsed", (string?)switcher.Attribute("Visibility"));

        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactDocumentSectionButton",
            "Click",
            "ShowCompactDocumentSectionButton_Click");
        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactDocumentSectionButton",
            "Width",
            "82");
        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactLineEntrySectionButton",
            "Click",
            "ShowCompactLineEntrySectionButton_Click");
        AssertNamedElementAttribute(
            document,
            xaml,
            "ShowCompactLineEntrySectionButton",
            "Width",
            "82");

        Assert.DoesNotContain(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "SalesAutoSaveNoticeText",
                StringComparison.Ordinal));
        Assert.DoesNotContain(
            document.Descendants(),
            element => ((string?)element.Attribute("Text"))?.Contains(
                "이 화면은 자동저장 방식으로 동작합니다.",
                StringComparison.Ordinal) == true);

        var rentalNotice = AssertNamedElement(
            document,
            xaml,
            "SalesRentalLinkedNoticeText");
        Assert.Equal("Wrap", (string?)rentalNotice.Attribute("TextWrapping"));
        Assert.Equal(
            "None",
            (string?)rentalNotice.Attribute("TextTrimming"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)rentalNotice.Attribute("ToolTip")));

        var rentalReference = AssertNamedElement(
            document,
            xaml,
            "SalesRentalLinkedReferenceText");
        Assert.Equal(
            "None",
            (string?)rentalReference.Attribute("TextTrimming"));
        Assert.False(
            string.IsNullOrWhiteSpace(
                (string?)rentalReference.Attribute("ToolTip")));
    }

    private static void AssertSalesHeaderActions(
        XDocument document,
        XNamespace xaml)
    {
        var actions = AssertNamedElement(
            document,
            xaml,
            "SalesHeaderActions");
        Assert.Equal("Right", (string?)actions.Attribute("HorizontalAlignment"));

        var headerGrid = Assert.IsType<XElement>(actions.Parent);
        var columns = headerGrid
            .Elements()
            .Single(element => element.Name.LocalName == "Grid.ColumnDefinitions")
            .Elements()
            .ToArray();
        Assert.Collection(
            columns,
            titleColumn => Assert.Equal("*", (string?)titleColumn.Attribute("Width")),
            actionColumn => Assert.Equal("2*", (string?)actionColumn.Attribute("Width")));

        var buttons = actions
            .Descendants()
            .Where(element => element.Name.LocalName == "Button")
            .ToArray();
        Assert.Contains(
            buttons,
            button => string.Equals(
                (string?)button.Attribute("Command"),
                "{Binding StartNewInvoiceCommand}",
                StringComparison.Ordinal));
        Assert.Contains(
            buttons,
            button =>
                string.Equals(
                    (string?)button.Attribute(xaml + "Name"),
                    "SalesRentalLinkedSaveButton",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)button.Attribute("Content"),
                    "렌탈 전표 반영",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)button.Attribute("Command"),
                    "{Binding SaveCommand}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    (string?)button.Attribute("Visibility"),
                    "{Binding IsRentalBillingLinkedInvoice, Converter={StaticResource BoolToVisibilityConverter}}",
                    StringComparison.Ordinal));
        Assert.DoesNotContain(
            buttons,
            button => string.Equals(
                (string?)button.Attribute("Content"),
                "저장",
                StringComparison.Ordinal));
        Assert.Contains(
            buttons,
            button => string.Equals(
                (string?)button.Attribute("Click"),
                "PaymentButton_Click",
                StringComparison.Ordinal));
        Assert.Contains(
            buttons,
            button => string.Equals(
                (string?)button.Attribute("Click"),
                "CloseButton_Click",
                StringComparison.Ordinal));
    }

    private static void AssertDataGridVirtualizationStyle(XDocument document)
    {
        var style = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "Style" &&
                string.Equals(
                    (string?)element.Attribute("TargetType"),
                    "DataGrid",
                    StringComparison.Ordinal));
        var setters = style
            .Elements()
            .Where(element => element.Name.LocalName == "Setter")
            .ToDictionary(
                element => (string?)element.Attribute("Property") ?? string.Empty,
                element => (string?)element.Attribute("Value") ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("True", setters["EnableRowVirtualization"]);
        Assert.Equal("True", setters["EnableColumnVirtualization"]);
        Assert.Equal("True", setters["VirtualizingPanel.IsVirtualizing"]);
        Assert.Equal("Recycling", setters["VirtualizingPanel.VirtualizationMode"]);
        Assert.Equal("True", setters["ScrollViewer.CanContentScroll"]);
    }

    private static void AssertResponsiveMinimum(XElement? root)
        => AssertWindowMinimum(
            root,
            ChildWindowResponsiveLayoutPolicy.MinimumWidthDip,
            ChildWindowResponsiveLayoutPolicy.MinimumHeightDip);

    private static void AssertResponsiveModalShell(
        XDocument document,
        XNamespace xaml,
        string scrollViewerName,
        string contentName,
        string footerName,
        double minimumContentWidth)
    {
        var window = Assert.IsType<XElement>(document.Root);
        AssertResponsiveMinimum(window);
        Assert.Equal(
            "CanResize",
            (string?)window.Attribute("ResizeMode"));

        var shell = Assert.Single(
            window.Elements(),
            element => element.Name.LocalName == "Grid");
        var rowDefinitions = Assert.Single(
                shell.Elements(),
                element => element.Name.LocalName == "Grid.RowDefinitions")
            .Elements()
            .Where(element => element.Name.LocalName == "RowDefinition")
            .Select(element => (string?)element.Attribute("Height"))
            .ToArray();
        Assert.Equal(new[] { "Auto", "*", "Auto" }, rowDefinitions);

        var scrollViewer = AssertNamedElement(
            document,
            xaml,
            scrollViewerName);
        Assert.Equal("ScrollViewer", scrollViewer.Name.LocalName);
        Assert.Equal(
            "Auto",
            (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal(
            "Auto",
            (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.Contains(
            scrollViewer.AncestorsAndSelf(),
            element =>
                ReferenceEquals(element.Parent, shell) &&
                string.Equals(
                    (string?)element.Attribute("Grid.Row"),
                    "1",
                    StringComparison.Ordinal));

        var content = AssertNamedElement(document, xaml, contentName);
        Assert.Contains(
            content.Ancestors(),
            ancestor => ReferenceEquals(ancestor, scrollViewer));
        var declaredContentWidth = double.Parse(
            Assert.IsType<XAttribute>(content.Attribute("MinWidth")).Value,
            CultureInfo.InvariantCulture);
        Assert.Equal(minimumContentWidth, declaredContentWidth);
        Assert.Equal(
            $"{{Binding ViewportWidth, ElementName={scrollViewerName}}}",
            (string?)content.Attribute("Width"));

        var footer = AssertNamedElement(document, xaml, footerName);
        Assert.Same(shell, footer.Parent);
        Assert.Equal("2", (string?)footer.Attribute("Grid.Row"));
        Assert.DoesNotContain(
            footer.Ancestors(),
            ancestor => ReferenceEquals(ancestor, scrollViewer));
    }

    private static string RemoveWhitespace(string source) =>
        new(source.Where(character => !char.IsWhiteSpace(character)).ToArray());

    private static void AssertWindowMinimum(
        XElement? root,
        double expectedWidth,
        double expectedHeight)
    {
        var window = Assert.IsType<XElement>(root);
        Assert.Equal(
            expectedWidth.ToString("0"),
            (string?)window.Attribute("MinWidth"));
        Assert.Equal(
            expectedHeight.ToString("0"),
            (string?)window.Attribute("MinHeight"));
    }

    private static void AssertScrollViewer(
        XDocument document,
        XNamespace xaml,
        string name,
        string horizontal,
        string vertical)
    {
        var scrollViewer = Assert.Single(
            document.Descendants(),
            element =>
                element.Name.LocalName == "ScrollViewer" &&
                string.Equals(
                    (string?)element.Attribute(xaml + "Name"),
                    name,
                    StringComparison.Ordinal));
        Assert.Equal(
            horizontal,
            (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal(
            vertical,
            (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
    }

    private static void AssertNamedElementAttribute(
        XDocument document,
        XNamespace xaml,
        string name,
        string attributeName,
        string expected)
    {
        var element = Assert.Single(
            document.Descendants(),
            candidate => string.Equals(
                (string?)candidate.Attribute(xaml + "Name"),
                name,
                StringComparison.Ordinal));
        Assert.Equal(expected, (string?)element.Attribute(attributeName));
    }

    private static XElement AssertNamedElement(
        XDocument document,
        XNamespace xaml,
        string name) =>
        Assert.Single(
            document.Descendants(),
            candidate => string.Equals(
                (string?)candidate.Attribute(xaml + "Name"),
                name,
                StringComparison.Ordinal));

    private static void AssertNamedElementType(
        XDocument document,
        XNamespace xaml,
        string name,
        string expectedType)
    {
        var element = Assert.Single(
            document.Descendants(),
            candidate => string.Equals(
                (string?)candidate.Attribute(xaml + "Name"),
                name,
                StringComparison.Ordinal));
        Assert.Equal(expectedType, element.Name.LocalName);
    }

    private static XDocument LoadWindow(
        string desktopAppDirectory,
        string fileName) =>
        XDocument.Load(
            Path.Combine(desktopAppDirectory, "Views", fileName));

    private static string FindDesktopAppDirectory()
    {
        var root = FindRepositoryRoot();
        return Directory
            .GetDirectories(Path.Combine(root, "Desktop"), "*.Desktop.App")
            .Single();
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "Desktop")) &&
                Directory.Exists(Path.Combine(current.FullName, "Tests")) &&
                Directory.GetFiles(current.FullName, "*.sln").Length > 0)
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
