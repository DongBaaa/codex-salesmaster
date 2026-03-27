using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.EntityFrameworkCore;
using 거래플랜.Desktop.App;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        PrepareIsolatedLocalAppData();
        var app = new App();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Application.Current.Resources["UnifiedDatePickerButtonStyle"] = BuildUnifiedDatePickerButtonStyle();

        using var db = new LocalDbContext();
        LocalDbInitializer.InitializeAsync(db).GetAwaiter().GetResult();

        var session = CreateAdminSession();

        var syncDispatcher = new SyncRequestDispatcher();
        var local = new LocalStateService(db, new OfficeAccessService(), syncDispatcher, session);
        var statementPrintService = new StatementPrintService();
        var invoicePrintService = new WpfInvoicePrintService();

        var fixture = SeedFixtureAsync(db, local, session).GetAwaiter().GetResult();

        var officeAccess = new OfficeAccessService();
        var itworldSession = CreateOfflineSession(
            "itworld-user",
            DomainConstants.OfficeItworld,
            DomainConstants.RoleUser,
            TenantScopeCatalog.Itworld,
            TenantScopeCatalog.ScopeTenantAll);
        var usenetSession = CreateOfflineSession(
            "usenet-user",
            DomainConstants.OfficeUsenet,
            DomainConstants.RoleUser,
            TenantScopeCatalog.UsenetGroup,
            TenantScopeCatalog.ScopeTenantAll);
        var itworldLocal = new LocalStateService(db, officeAccess, syncDispatcher, itworldSession);
        var usenetLocal = new LocalStateService(db, officeAccess, syncDispatcher, usenetSession);

        var paymentAdvanceVm = new PaymentViewModel(local, session);
        paymentAdvanceVm.LoadAsync(fixture.Customer).GetAwaiter().GetResult();
        var transactionKindCycle = new[]
        {
            PaymentFlowConstants.TransactionKindReceipt,
            PaymentFlowConstants.TransactionKindPayment,
            PaymentFlowConstants.TransactionKindAdvanceDeposit,
            PaymentFlowConstants.TransactionKindAdvanceRefund
        };
        foreach (var kind in transactionKindCycle)
            paymentAdvanceVm.SelectedTransactionKind = kind;
        paymentAdvanceVm.SelectedTransactionKind = PaymentFlowConstants.TransactionKindAdvanceDeposit;
        var paymentAdvanceWindow = new PaymentWindow(paymentAdvanceVm);
        PrepareWindow(paymentAdvanceWindow);
        CycleTransactionKinds(paymentAdvanceVm, transactionKindCycle, PaymentFlowConstants.TransactionKindAdvanceDeposit);

        var paymentInvoiceVm = new PaymentViewModel(local, session);
        paymentInvoiceVm.LoadAsync(fixture.Customer).GetAwaiter().GetResult();
        paymentInvoiceVm.ConfigureForInvoiceAsync(fixture.PurchaseInvoice).GetAwaiter().GetResult();
        var paymentInvoiceWindow = new PaymentWindow(paymentInvoiceVm);
        PrepareWindow(paymentInvoiceWindow);

        var paymentItworldVm = new PaymentViewModel(itworldLocal, itworldSession);
        paymentItworldVm.LoadAsync(fixture.ItworldCustomer).GetAwaiter().GetResult();
        foreach (var kind in transactionKindCycle)
            paymentItworldVm.SelectedTransactionKind = kind;
        paymentItworldVm.SelectedTransactionKind = PaymentFlowConstants.TransactionKindPayment;
        var paymentItworldWindow = new PaymentWindow(paymentItworldVm);
        PrepareWindow(paymentItworldWindow);
        CycleTransactionKinds(paymentItworldVm, transactionKindCycle, PaymentFlowConstants.TransactionKindPayment);

        var paymentUsenetVm = new PaymentViewModel(usenetLocal, usenetSession);
        paymentUsenetVm.LoadAsync(fixture.UsenetCustomer).GetAwaiter().GetResult();
        foreach (var kind in transactionKindCycle)
            paymentUsenetVm.SelectedTransactionKind = kind;
        paymentUsenetVm.SelectedTransactionKind = PaymentFlowConstants.TransactionKindPayment;
        var paymentUsenetWindow = new PaymentWindow(paymentUsenetVm);
        PrepareWindow(paymentUsenetWindow);
        CycleTransactionKinds(paymentUsenetVm, transactionKindCycle, PaymentFlowConstants.TransactionKindPayment);

        var salesVm = new SalesViewModel(local, statementPrintService, invoicePrintService, session, VoucherType.Sales);
        salesVm.LoadAsync().GetAwaiter().GetResult();
        salesVm.LoadInvoiceAsync(fixture.SalesInvoice).GetAwaiter().GetResult();
        var salesWindow = new SalesWindow(salesVm);
        PrepareWindow(salesWindow);

        var purchaseVm = new SalesViewModel(local, statementPrintService, invoicePrintService, session, VoucherType.Purchase);
        purchaseVm.LoadAsync().GetAwaiter().GetResult();
        purchaseVm.LoadInvoiceAsync(fixture.PurchaseInvoice).GetAwaiter().GetResult();
        var purchaseWindow = new SalesWindow(purchaseVm);
        PrepareWindow(purchaseWindow);

        var transferVm = new InventoryTransferViewModel(local, session);
        transferVm.LoadAsync().GetAwaiter().GetResult();
        var transferWindow = new InventoryTransferWindow(transferVm);
        PrepareWindow(transferWindow);

        var paymentAdvanceButtons = CollectButtons(paymentAdvanceWindow);
        var paymentInvoiceButtons = CollectButtons(paymentInvoiceWindow);
        var paymentItworldButtons = CollectButtons(paymentItworldWindow);
        var paymentUsenetButtons = CollectButtons(paymentUsenetWindow);
        var salesButtons = CollectButtons(salesWindow);
        var purchaseButtons = CollectButtons(purchaseWindow);
        var transferButtons = CollectButtons(transferWindow);
        var transferGrid = FindDataGrids(transferWindow).LastOrDefault();

        var report = new
        {
            Payment = new
            {
                AdvanceDeposit = new
                {
                    Title = paymentAdvanceWindow.Title,
                    Kind = paymentAdvanceVm.SelectedTransactionKind,
                    ContextSummary = paymentAdvanceVm.TransactionContextSummary,
                    ContextDetail = paymentAdvanceVm.TransactionSummary,
                    AdvanceBalance = paymentAdvanceVm.AdvanceBalance,
                    SummaryLabel = paymentAdvanceVm.PaymentActionLabel,
                    TransactionKindCycle = transactionKindCycle,
                    HistoryColumns = FindDataGrids(paymentAdvanceWindow).FirstOrDefault()?.Columns.Count ?? 0,
                    AttachmentColumns = FindDataGrids(paymentAdvanceWindow).Skip(1).FirstOrDefault()?.Columns.Count ?? 0,
                    Buttons = new[] { "증빙 첨부", "미리보기", "삭제", "확인완료", "반려" }
                        .ToDictionary(text => text, text => paymentAdvanceButtons.Contains(text))
                },
                InvoiceLinkedSettlement = new
                {
                    Title = paymentInvoiceWindow.Title,
                    Kind = paymentInvoiceVm.SelectedTransactionKind,
                    ContextSummary = paymentInvoiceVm.TransactionContextSummary,
                    ContextDetail = paymentInvoiceVm.TransactionSummary,
                    AdvanceBalance = paymentInvoiceVm.AdvanceBalance,
                    SummaryLabel = paymentInvoiceVm.PaymentActionLabel,
                    Buttons = new[] { "증빙 첨부", "미리보기", "삭제", "확인완료", "반려" }
                        .ToDictionary(text => text, text => paymentInvoiceButtons.Contains(text))
                },
                ItworldGeneralPayment = new
                {
                    Title = paymentItworldWindow.Title,
                    Office = itworldSession.OfficeCode,
                    Kind = paymentItworldVm.SelectedTransactionKind,
                    ContextSummary = paymentItworldVm.TransactionContextSummary,
                    ContextDetail = paymentItworldVm.TransactionSummary,
                    AdvanceBalance = paymentItworldVm.AdvanceBalance,
                    SummaryLabel = paymentItworldVm.PaymentActionLabel,
                    TransactionKindCycle = transactionKindCycle,
                    Buttons = new[] { "증빙 첨부", "미리보기", "삭제", "확인완료", "반려" }
                        .ToDictionary(text => text, text => paymentItworldButtons.Contains(text))
                },
                UsenetGeneralPayment = new
                {
                    Title = paymentUsenetWindow.Title,
                    Office = usenetSession.OfficeCode,
                    Kind = paymentUsenetVm.SelectedTransactionKind,
                    ContextSummary = paymentUsenetVm.TransactionContextSummary,
                    ContextDetail = paymentUsenetVm.TransactionSummary,
                    AdvanceBalance = paymentUsenetVm.AdvanceBalance,
                    SummaryLabel = paymentUsenetVm.PaymentActionLabel,
                    TransactionKindCycle = transactionKindCycle,
                    Buttons = new[] { "증빙 첨부", "미리보기", "삭제", "확인완료", "반려" }
                        .ToDictionary(text => text, text => paymentUsenetButtons.Contains(text))
                }
            },
            Sales = new
            {
                SalesDocument = new
                {
                    Title = salesWindow.Title,
                    PaymentButtonText = salesVm.PaymentActionButtonText,
                    PaymentButtonPresent = salesButtons.Contains(salesVm.PaymentActionButtonText),
                    SummaryTitle = salesVm.PaymentSummaryTitleText,
                    SummaryContext = salesVm.PaymentSummaryContextText,
                    SummaryDetail = salesVm.PaymentSummaryDetailText,
                    SummaryAdvance = salesVm.PaymentSummaryAdvanceText,
                    Width = salesWindow.Width,
                    Height = salesWindow.Height
                },
                PurchaseDocument = new
                {
                    Title = purchaseWindow.Title,
                    PaymentButtonText = purchaseVm.PaymentActionButtonText,
                    PaymentButtonPresent = purchaseButtons.Contains(purchaseVm.PaymentActionButtonText),
                    SummaryTitle = purchaseVm.PaymentSummaryTitleText,
                    SummaryContext = purchaseVm.PaymentSummaryContextText,
                    SummaryDetail = purchaseVm.PaymentSummaryDetailText,
                    SummaryAdvance = purchaseVm.PaymentSummaryAdvanceText,
                    Width = purchaseWindow.Width,
                    Height = purchaseWindow.Height
                }
            },
            Transfer = new
            {
                Title = transferWindow.Title,
                ActionButtons = new[] { "수령확정", "반려", "행 추가", "행 수정", "행 삭제" }
                    .ToDictionary(text => text, text => transferButtons.Contains(text)),
                RecentColumns = FindDataGrids(transferWindow).FirstOrDefault()?.Columns.Count ?? 0,
                LineColumns = transferGrid?.Columns.Count ?? 0,
                Width = transferWindow.Width,
                Height = transferWindow.Height
            }
        };

        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void PrepareIsolatedLocalAppData()
    {
        var runtimeRoot = Path.Combine(AppContext.BaseDirectory, "runtime", "isolated-payment-transfer-verifier");
        var localAppData = Path.Combine(runtimeRoot, "LocalAppData");
        var georaePlanRoot = Path.Combine(runtimeRoot, "거래플랜");

        Environment.SetEnvironmentVariable("GEORAEPLAN_APP_ROOT", georaePlanRoot, EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_LEGACY_MERGE", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("GEORAEPLAN_DISABLE_SERVER_SYNC", "1", EnvironmentVariableTarget.Process);
        Environment.SetEnvironmentVariable("LOCALAPPDATA", localAppData, EnvironmentVariableTarget.Process);

        if (Directory.Exists(runtimeRoot))
            Directory.Delete(runtimeRoot, recursive: true);

        if (Directory.Exists(georaePlanRoot))
            Directory.Delete(georaePlanRoot, recursive: true);

        Directory.CreateDirectory(localAppData);
        Directory.CreateDirectory(georaePlanRoot);
    }

    private static Style BuildUnifiedDatePickerButtonStyle()
    {
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
        return style;
    }

    private static async Task<SeedFixture> SeedFixtureAsync(LocalDbContext db, LocalStateService local, SessionState session)
    {
        var customer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "결제검증 거래처",
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            Phone = "010-1234-5678",
            MobilePhone = "010-9876-5432"
        };
        await local.UpsertCustomerAsync(customer, session);

        var itworldCustomer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "아이티월드 결제검증 거래처",
            ResponsibleOfficeCode = DomainConstants.OfficeItworld,
            Phone = "[REDACTED_PHONE]",
            MobilePhone = "010-1000-1000"
        };
        await local.UpsertCustomerAsync(itworldCustomer, session);

        var usenetCustomer = new LocalCustomer
        {
            Id = Guid.NewGuid(),
            NameOriginal = "유즈넷 결제검증 거래처",
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            Phone = "032-999-9999",
            MobilePhone = "010-2000-2000"
        };
        await local.UpsertCustomerAsync(usenetCustomer, session);

        var salesItem = new LocalItem
        {
            Id = Guid.NewGuid(),
            NameOriginal = "결제검증 품목-매출",
            SpecificationOriginal = "규격-A",
            Unit = "EA"
        };
        await local.UpsertItemAsync(salesItem);

        var purchaseItem = new LocalItem
        {
            Id = Guid.NewGuid(),
            NameOriginal = "결제검증 품목-매입",
            SpecificationOriginal = "규격-B",
            Unit = "EA"
        };
        await local.UpsertItemAsync(purchaseItem);

        var salesInvoice = await SaveInvoiceAsync(
            db,
            local,
            session,
            customer,
            VoucherType.Sales,
            "매출 전표",
            salesItem,
            12000m,
            12m);

        var purchaseInvoice = await SaveInvoiceAsync(
            db,
            local,
            session,
            customer,
            VoucherType.Purchase,
            "매입 전표",
            purchaseItem,
            10000m,
            10m);

        return new SeedFixture(customer, itworldCustomer, usenetCustomer, salesItem, purchaseItem, salesInvoice, purchaseInvoice);
    }

    private static async Task<LocalInvoice> SaveInvoiceAsync(
        LocalDbContext db,
        LocalStateService local,
        SessionState session,
        LocalCustomer customer,
        VoucherType voucherType,
        string memo,
        LocalItem item,
        decimal unitPrice,
        decimal quantity)
    {
        var invoice = new LocalInvoice
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            VoucherType = voucherType,
            InvoiceDate = DateOnly.FromDateTime(DateTime.Today),
            Memo = memo,
            ResponsibleOfficeCode = DomainConstants.OfficeUsenet,
            SourceWarehouseCode = DomainConstants.WarehouseUsenetMain,
            Lines = new List<LocalInvoiceLine>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    ItemId = item.Id,
                    ItemNameOriginal = item.NameOriginal,
                    SpecificationOriginal = item.SpecificationOriginal,
                    Unit = item.Unit,
                    Quantity = quantity,
                    UnitPrice = unitPrice,
                    LineAmount = quantity * unitPrice
                }
            }
        };

        var result = await local.SaveInvoiceAsync(
            invoice,
            new InvoiceSaveContext
            {
                Username = "admin",
                Role = DomainConstants.RoleAdmin,
                OfficeCode = DomainConstants.OfficeUsenet
            },
            session);

        if (!result.Success)
            throw new InvalidOperationException(result.Message);

        var saved = await local.GetInvoiceAsync(result.SavedInvoiceId, session);
        if (saved is null)
            throw new InvalidOperationException("저장된 전표를 다시 불러오지 못했습니다.");

        db.ChangeTracker.Clear();
        return saved;
    }

    private static void PrepareWindow(Window window)
    {
        window.ShowInTaskbar = false;
        window.WindowStyle = WindowStyle.ToolWindow;
        window.Show();
        window.ApplyTemplate();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        window.UpdateLayout();
    }

    private static HashSet<string> CollectButtons(DependencyObject root)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        Visit(root, result);
        return result;

        static void Visit(DependencyObject node, HashSet<string> bucket)
        {
            if (node is Button button && button.Content is string text && !string.IsNullOrWhiteSpace(text))
                bucket.Add(text);

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Visit(VisualTreeHelper.GetChild(node, i), bucket);
        }
    }

    private static List<DataGrid> FindDataGrids(DependencyObject root)
    {
        var result = new List<DataGrid>();
        Visit(root, result);
        return result;

        static void Visit(DependencyObject node, List<DataGrid> bucket)
        {
            if (node is DataGrid grid)
                bucket.Add(grid);

            var count = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < count; i++)
                Visit(VisualTreeHelper.GetChild(node, i), bucket);
        }
    }

    private static void CycleTransactionKinds(PaymentViewModel viewModel, IReadOnlyList<string> kinds, string finalKind)
    {
        foreach (var kind in kinds)
            viewModel.SelectedTransactionKind = kind;

        viewModel.SelectedTransactionKind = finalKind;
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

    private static SessionState CreateOfflineSession(
        string username,
        string officeCode,
        string role,
        string? tenantCode = null,
        string? scopeType = null)
    {
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto
        {
            UserId = Guid.NewGuid(),
            Username = username,
            Role = role,
            TenantCode = tenantCode ?? TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, officeCode),
            OfficeCode = officeCode,
            ScopeType = scopeType ?? TenantScopeCatalog.ScopeOfficeOnly,
            Permissions = new List<string>()
        });
        return session;
    }

    private sealed record SeedFixture(
        LocalCustomer Customer,
        LocalCustomer ItworldCustomer,
        LocalCustomer UsenetCustomer,
        LocalItem SalesItem,
        LocalItem PurchaseItem,
        LocalInvoice SalesInvoice,
        LocalInvoice PurchaseInvoice);
}
