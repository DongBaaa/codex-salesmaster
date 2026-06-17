using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using \uAC70\uB798\uD50C\uB79C.Desktop.App;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Data;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Printing;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Services;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.ViewModels;
using \uAC70\uB798\uD50C\uB79C.Desktop.App.Views;
using \uAC70\uB798\uD50C\uB79C.Shared.Contracts;

internal static class Program
{
    private const string StatusWarehouse = "\uCC3D\uACE0";
    private const string StatusInstalled = "\uC124\uCE58\uC911";

    private static readonly IReadOnlyDictionary<string, int> RequiredWindowDatePickerCounts = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["customer-edit"] = 3,
        ["inventory-transfer"] = 1,
        ["period-ledger"] = 2,
        ["print-edit"] = 1,
        ["rental-asset-link"] = 1,
        ["rental-asset"] = 5,
        ["rental-assignment-history-edit"] = 2,
        ["rental-billing"] = 2,
        ["rental-contract-editor"] = 3,
        ["rental-customer-onboarding"] = 1,
        ["rental-equipment-replacement"] = 1,
        ["yeonsu-delivery"] = 2
    };

    [STAThread]
    private static void Main()
    {
        PrepareIsolatedLocalAppData();
        var app = new App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        EnsureUnifiedDatePickerButtonStyle();

        using var db = new LocalDbContext();
        LocalDbInitializer.InitializeAsync(db).GetAwaiter().GetResult();

        var session = CreateAdminSession();
        var syncDispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), syncDispatcher, session);
        var rental = new RentalStateService(db, local);
        var statementPrintService = new StatementPrintService();
        var invoicePrintService = new WpfInvoicePrintService();
        var rentalDocumentService = new RentalDocumentService();

        SeedReferenceDataAsync(local, session).GetAwaiter().GetResult();

        var auditWindows = CreateAuditWindows(
            local,
            rental,
            statementPrintService,
            invoicePrintService,
            rentalDocumentService,
            session);

        var evidenceDirectory = PrepareEvidenceDirectory();
        var screenshots = new List<WindowScreenshot>();
        var datePickerMetrics = new List<DatePickerRuntimeMetric>();

        foreach (var auditWindow in auditWindows)
        {
            PrepareWindow(auditWindow.Window);
            screenshots.Add(CaptureWindow(auditWindow.Window, evidenceDirectory, auditWindow.Key));
            datePickerMetrics.AddRange(CollectDatePickerMetrics(auditWindow.Window, auditWindow.Key));
        }

        ValidateDatePickerMetrics(datePickerMetrics);

        var report = new
        {
            Summary = new
            {
                AuditedWindowCount = auditWindows.Count,
                RequiredDatePickerCount = RequiredWindowDatePickerCounts.Values.Sum(),
                MeasuredDatePickerCount = datePickerMetrics.Count,
                EvidenceDirectory = evidenceDirectory,
                CoveredWindows = RequiredWindowDatePickerCounts.Keys.ToArray(),
                ComplementsExistingPaymentTransferVerifier = true
            },
            Screenshots = screenshots,
            DatePickers = datePickerMetrics
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.Out.Flush();
        Environment.Exit(0);
    }

    private static List<AuditWindow> CreateAuditWindows(
        LocalStateService local,
        RentalStateService rental,
        StatementPrintService statementPrintService,
        IPrintService invoicePrintService,
        RentalDocumentService rentalDocumentService,
        SessionState session)
    {
        var windows = new List<AuditWindow>();

        var customerEditVm = new CustomerEditViewModel(local, session);
        customerEditVm.LoadAsync().GetAwaiter().GetResult();
        customerEditVm.ContractSignedDate = DateOnly.FromDateTime(DateTime.Today);
        customerEditVm.ContractExpireDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(12));
        windows.Add(new AuditWindow("customer-edit", new CustomerEditWindow(customerEditVm)));

        var transferVm = new InventoryTransferViewModel(local, session);
        transferVm.LoadAsync().GetAwaiter().GetResult();
        windows.Add(new AuditWindow("inventory-transfer", new InventoryTransferWindow(transferVm)));

        var periodLedgerVm = new PeriodLedgerViewModel(
            local,
            new PeriodLedgerAggregationService(local),
            new PeriodLedgerExcelExportService(),
            session);
        periodLedgerVm.InitializeAsync().GetAwaiter().GetResult();
        windows.Add(new AuditWindow("period-ledger", new PeriodLedgerWindow(periodLedgerVm)));

        var printEditVm = new PrintEditViewModel(
            BuildInvoicePrintModel(),
            _ => Task.CompletedTask,
            (_, _) => new FixedDocument());
        windows.Add(new AuditWindow("print-edit", new PrintEditWindow(printEditVm)));

        var rentalAssetLinkVm = new RentalAssetLinkDialogViewModel(
            rental,
            session,
            currentBillingProfileId: Guid.NewGuid(),
            currentCustomerId: Guid.NewGuid(),
            currentCustomerName: "Runtime rental customer",
            currentOfficeCode: DomainConstants.OfficeUsenet,
            defaultInstallLocation: "Runtime install location")
        {
            SelectedAsset = BuildRentalBillingAssetOption()
        };
        rentalAssetLinkVm.Assets.Add(rentalAssetLinkVm.SelectedAsset);
        windows.Add(new AuditWindow("rental-asset-link", new RentalAssetLinkDialog(rentalAssetLinkVm)));

        var rentalAssetVm = new RentalAssetViewModel(rental, local, rentalDocumentService, invoicePrintService, session)
        {
            EditPurchaseDate = DateTime.Today,
            EditContractDate = DateTime.Today,
            EditInstallDate = DateTime.Today,
            EditContractStartDate = DateTime.Today,
            EditRentalEndDate = DateTime.Today.AddMonths(12)
        };
        windows.Add(new AuditWindow("rental-asset", new RentalAssetWindow(rentalAssetVm)));

        windows.Add(new AuditWindow(
            "rental-assignment-history-edit",
            new RentalAssignmentHistoryEditWindow(new RentalAssetAssignmentHistoryEditRequest
            {
                AssetId = Guid.NewGuid(),
                IsCurrent = false,
                LinkedAtLocal = DateTime.Today.AddMonths(-1),
                UnlinkedAtLocal = DateTime.Today,
                CustomerName = "Runtime rental customer",
                InstallLocation = "Runtime install location",
                ItemName = "Runtime equipment",
                MachineNumber = "RT-001",
                ManagementNumber = "M-001",
                ChangeReason = "Runtime audit"
            })));

        var rentalBillingVm = new RentalBillingViewModel(rental, local, session)
        {
            EditContractDate = DateTime.Today
        };
        windows.Add(new AuditWindow("rental-billing", new RentalBillingWindow(rentalBillingVm)));

        var rentalContractVm = new RentalContractEditorViewModel(
            BuildRentalContractDocumentModel(),
            rentalDocumentService);
        windows.Add(new AuditWindow("rental-contract-editor", new RentalContractEditorWindow(rentalContractVm)));

        var onboardingVm = new RentalCustomerOnboardingViewModel(rental, local, session)
        {
            CurrentStepIndex = 2,
            BillingStartDate = DateTime.Today,
            CustomerName = "Runtime rental customer",
            OfficeCode = DomainConstants.OfficeUsenet
        };
        windows.Add(new AuditWindow("rental-customer-onboarding", new RentalCustomerOnboardingWindow(onboardingVm)));

        var originalAsset = BuildLocalRentalAsset("Original equipment", "ORG-001");
        var replacementAsset = BuildLocalRentalAsset("Replacement equipment", "NEW-001");
        windows.Add(new AuditWindow(
            "rental-equipment-replacement",
            new RentalEquipmentReplacementWindow(originalAsset, replacementAsset, new RentalEquipmentReplacementRequest
            {
                OriginalAssetId = originalAsset.Id,
                ReplacementAssetId = replacementAsset.Id,
                ReplacementDate = DateOnly.FromDateTime(DateTime.Today),
                OriginalAssetNextStatus = StatusWarehouse,
                ChangeReason = "Runtime audit"
            })));

        var yeonsuVm = new YeonsuDeliveryViewModel(local, session);
        windows.Add(new AuditWindow(
            "yeonsu-delivery",
            new YeonsuDeliveryWindow(yeonsuVm, local, statementPrintService, invoicePrintService, session)));

        return windows;
    }

    private static async Task SeedReferenceDataAsync(LocalStateService local, SessionState session)
    {
        await local.UpsertCustomerAsync(new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "Runtime UI audit customer",
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            Phone = "010-0000-0000",
            MobilePhone = "010-1111-1111"
        }, session);

        await local.UpsertItemAsync(new LocalItem
        {
            Id = Guid.NewGuid(),
            NameOriginal = "Runtime UI audit item",
            SpecificationOriginal = "Spec",
            Unit = "EA"
        });
    }

    private static InvoicePrintModel BuildInvoicePrintModel()
    {
        var model = new InvoicePrintModel
        {
            InvoiceId = Guid.NewGuid(),
            InvoiceNumber = "RT-PRINT-001",
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            VoucherType = VoucherType.Sales.ToString(),
            SupplierName = "Runtime supplier",
            BuyerName = "Runtime buyer",
            TotalAmount = 10000m
        };
        model.Lines.Add(new InvoicePrintLineModel
        {
            No = 1,
            ItemName = "Runtime item",
            Specification = "Spec",
            Unit = "EA",
            Quantity = 1m,
            UnitPrice = 10000m,
            Amount = 10000m
        });
        return model;
    }

    private static RentalContractDocumentModel BuildRentalContractDocumentModel()
        => new()
        {
            TenantName = "Runtime tenant",
            CompanyName = "Runtime rental company",
            ManagementNumber = "RC-001",
            ItemName = "Runtime equipment",
            MachineNumber = "SN-001",
            ContractDate = DateOnly.FromDateTime(DateTime.Today),
            ContractStartDate = DateOnly.FromDateTime(DateTime.Today),
            ContractEndDate = DateOnly.FromDateTime(DateTime.Today.AddMonths(12))
        };

    private static RentalBillingAssetOption BuildRentalBillingAssetOption()
        => new()
        {
            AssetId = Guid.NewGuid(),
            ManagementNumber = "RB-001",
            ItemName = "Runtime rental equipment",
            MachineNumber = "RB-SN-001",
            TargetCustomerName = "Runtime rental customer",
            CurrentCustomerName = "Runtime rental customer",
            InstallLocation = "Runtime install location",
            AssetStatus = StatusInstalled,
            BillingEligibilityStatus = "billable",
            CurrentBillingProfileDisplay = "Runtime billing profile",
            ResponsibleOfficeName = "USENET",
            ManagementCompanyName = "USENET",
            AssetScopeDisplay = "USENET",
            ContractStartDate = DateTime.Today,
            PurchaseDate = DateTime.Today.AddMonths(-2),
            InstallDate = DateTime.Today.AddMonths(-1),
            MonthlyFee = 10000m,
            IsSelected = true
        };

    private static LocalRentalAsset BuildLocalRentalAsset(string itemName, string managementNumber)
        => new()
        {
            Id = Guid.NewGuid(),
            ItemName = itemName,
            ManagementNumber = managementNumber,
            MachineNumber = managementNumber + "-SN",
            AssetStatus = StatusInstalled,
            CurrentCustomerName = "Runtime rental customer",
            CustomerName = "Runtime rental customer",
            InstallLocation = "Runtime install location",
            InstallSiteName = "Runtime install location"
        };

    private static void PrepareIsolatedLocalAppData()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "isolated-wpf-datepicker-runtime-audit");
        var localAppData = Path.Combine(runtimeRoot, "LocalAppData");
        var appRoot = Path.Combine(runtimeRoot, "GeoraePlan");

        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", appRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData, EnvironmentVariableTarget.Process);

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);

        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(appRoot);
    }

    private static void EnsureUnifiedDatePickerButtonStyle()
    {
        if (Application.Current.Resources.Contains("UnifiedDatePickerButtonStyle"))
            return;

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 34d));
        style.Setters.Add(new Setter(FrameworkElement.HeightProperty, 34d));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 34d));
        style.Setters.Add(new Setter(FrameworkElement.WidthProperty, 34d));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right));
        style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Stretch));
        Application.Current.Resources["UnifiedDatePickerButtonStyle"] = style;
    }

    private static void PrepareWindow(Window window)
    {
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.ToolWindow;
        window.Left = -20000;
        window.Top = -20000;

        var width = ResolveDimension(window.Width, window.MinWidth, 1280d);
        var height = ResolveDimension(window.Height, window.MinHeight, 820d);
        window.Width = width;
        window.Height = height;

        window.Show();
        window.ApplyTemplate();
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        window.UpdateLayout();
    }

    private static double ResolveDimension(double preferred, double minimum, double fallback)
    {
        if (!double.IsNaN(preferred) && !double.IsInfinity(preferred) && preferred > 0)
            return preferred;
        if (!double.IsNaN(minimum) && !double.IsInfinity(minimum) && minimum > 0)
            return Math.Max(minimum, fallback);
        return fallback;
    }

    private static string PrepareEvidenceDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "runtime", "wpf-datepicker-runtime-audit-evidence");
        if (Directory.Exists(directory))
            Directory.Delete(directory, recursive: true);

        Directory.CreateDirectory(directory);
        return directory;
    }

    private static WindowScreenshot CaptureWindow(Window window, string evidenceDirectory, string slug)
    {
        window.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth > 0 ? window.ActualWidth : window.Width));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight > 0 ? window.ActualHeight : window.Height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var path = Path.Combine(evidenceDirectory, slug + ".png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(path))
            encoder.Save(stream);

        var file = new FileInfo(path);
        if (file.Length <= 0)
            throw new InvalidOperationException($"Empty UI capture file: {path}");

        return new WindowScreenshot(slug, path, width, height, file.Length);
    }

    private static List<DatePickerRuntimeMetric> CollectDatePickerMetrics(Window window, string windowKey)
    {
        var result = new List<DatePickerRuntimeMetric>();
        var index = 0;
        foreach (var datePicker in FindVisualChildren<DatePicker>(window).Where(current => current.IsVisible))
        {
            index++;
            result.Add(new DatePickerRuntimeMetric(
                windowKey,
                datePicker.Name,
                index,
                Math.Round(datePicker.ActualWidth, 2),
                Math.Round(datePicker.ActualHeight, 2),
                Math.Round(datePicker.MinWidth, 2),
                Math.Round(datePicker.MinHeight, 2),
                datePicker.IsEnabled,
                datePicker.Text));
        }

        return result;
    }

    private static void ValidateDatePickerMetrics(IReadOnlyCollection<DatePickerRuntimeMetric> metrics)
    {
        foreach (var required in RequiredWindowDatePickerCounts)
        {
            var actual = metrics.Count(metric => string.Equals(metric.Window, required.Key, StringComparison.Ordinal));
            if (actual != required.Value)
            {
                throw new InvalidOperationException(
                    $"DatePicker runtime count mismatch: {required.Key} expected={required.Value}, actual={actual}");
            }
        }

        foreach (var metric in metrics)
        {
            if (metric.ActualWidth < 120 || metric.ActualHeight < 28)
            {
                throw new InvalidOperationException(
                    $"DatePicker too small: {metric.Window}#{metric.Index} {metric.ActualWidth}x{metric.ActualHeight}");
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
                yield return match;

            foreach (var descendant in FindVisualChildren<T>(child))
                yield return descendant;
        }
    }

    private static SessionState CreateAdminSession()
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = "admin",
            Role = DomainConstants.RoleAdmin,
            TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, DomainConstants.OfficeUsenet),
            OfficeCode = DomainConstants.OfficeUsenet,
            ScopeType = TenantScopeCatalog.ScopeAdmin,
            Permissions = new List<string>()
        });
        return session;
    }

    private sealed record AuditWindow(string Key, Window Window);

    private sealed record WindowScreenshot(
        string Window,
        string Path,
        int Width,
        int Height,
        long SizeBytes);

    private sealed record DatePickerRuntimeMetric(
        string Window,
        string Name,
        int Index,
        double ActualWidth,
        double ActualHeight,
        double MinWidth,
        double MinHeight,
        bool IsEnabled,
        string Text);
}