using System.Text.RegularExpressions;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class WpfGlobalUiGuardTests
{
    [Fact]
    public void MainWindow_InvoiceRows_ShowRentalBillTypeAndSettlementFontWeight()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml"));

        Assert.Contains("Binding=\"{Binding VoucherTypeDisplay}\" Width=\"100\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<DataGrid.RowStyle>", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"Normal\"/>", xaml, StringComparison.Ordinal);
        Assert.Contains("Binding=\"{Binding IsBalanceCleared}\" Value=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"FontWeight\" Value=\"Bold\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryViewDatePicker_KeepsCalendarPopupButtonStyleScopedToDatePickerButton()
    {
        var root = FindRepositoryRoot();
        var viewRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views");
        var failures = new List<string>();

        foreach (var xamlPath in Directory.EnumerateFiles(viewRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<DatePicker\\b[\\s\\S]*?</DatePicker>|<DatePicker\\b[^>]*/>",
                         RegexOptions.CultureInvariant))
            {
                var datePickerBlock = match.Value;
                if (datePickerBlock.Contains("<DatePicker.Resources>", StringComparison.Ordinal) &&
                    datePickerBlock.Contains("TargetType=\"{x:Type Button}\"", StringComparison.Ordinal) &&
                    datePickerBlock.Contains("BasedOn=\"{StaticResource", StringComparison.Ordinal) &&
                    datePickerBlock.Contains("DatePickerButtonStyle", StringComparison.Ordinal))
                {
                    continue;
                }

                failures.Add($"{RelativeToRoot(root, xamlPath)}:{GetLineNumber(xaml, match.Index)} DatePicker 버튼 스타일 범위가 누락되었습니다.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DatePickerStyles_KeepCalendarPopupWideEnoughAndDoNotLeakButtonWidthIntoCalendar()
    {
        var root = FindRepositoryRoot();
        var xamlFiles = Directory.EnumerateFiles(
                Path.Combine(root, "Desktop", "거래플랜.Desktop.App"),
                "*.xaml",
                SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                           !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        var failures = new List<string>();
        foreach (var xamlPath in xamlFiles)
        {
            var xaml = File.ReadAllText(xamlPath);
            foreach (Match match in Regex.Matches(
                         xaml,
                         "<Style\\s+TargetType=\"(?:\\{x:Type\\s+)?DatePicker(?:\\})?\"[^>]*>[\\s\\S]*?</Style>",
                         RegexOptions.CultureInvariant))
            {
                var styleBlock = match.Value;
                if (styleBlock.Contains("BasedOn=", StringComparison.Ordinal))
                    continue;

                if (!styleBlock.Contains("CalendarStyle", StringComparison.Ordinal) ||
                    !TryReadSetterInt(styleBlock, "MinWidth", out var minWidth) ||
                    minWidth < 150 ||
                    !TryReadSetterInt(styleBlock, "Width", out var width) ||
                    width < 150 ||
                    !TryReadSetterInt(styleBlock, "MaxWidth", out var maxWidth) ||
                    maxWidth < 150)
                {
                    failures.Add($"{RelativeToRoot(root, xamlPath)}:{GetLineNumber(xaml, match.Index)} DatePicker 스타일의 달력 폭/본문 폭 기준이 부족합니다.");
                }
            }
        }

        var appXaml = File.ReadAllText(Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "App.xaml"));
        var calendarItemBlock = ExtractBlock(
            appXaml,
            "<Style TargetType=\"{x:Type CalendarItem}\">",
            "<Style x:Key=\"UnifiedDatePickerButtonStyle\"");

        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"320\"/>", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinHeight\" Value=\"280\"/>", appXaml, StringComparison.Ordinal);
        Assert.Contains("<Style TargetType=\"{x:Type Button}\">", calendarItemBlock, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MinWidth\" Value=\"0\"/>", calendarItemBlock, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\"/>", calendarItemBlock, StringComparison.Ordinal);
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void DataIntegrityAlertWindow_KeepsScrollableBodyAndWiresVisibleActionButtons()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityAlertWindow.xaml"));
        var code = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DataIntegrityAlertWindow.xaml.cs"));

        Assert.Contains("MinWidth=\"920\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinHeight=\"560\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ScrollViewer Grid.Row=\"1\" VerticalScrollBarVisibility=\"Auto\">", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding Description}\" TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{Binding SuggestedAction}\" TextWrapping=\"Wrap\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"수정 화면 열기\" Click=\"FixButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Content=\"닫기(F12)\" Click=\"CloseButton_Click\"", xaml, StringComparison.Ordinal);
        Assert.Contains("NonClosingActionRequested.Invoke", code, StringComparison.Ordinal);
        Assert.Contains("DialogWindowCloseHelper.Close(this)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindowDashboardCards_DisplayCalculatedMetricsAndSafetyStockAlerts()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "MainViewModel.cs"));

        Assert.Contains("DashboardMetricCardStyle", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardMonthlySales", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardMonthlyAverageSales", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardMonthlyInvoiceCount", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardReceivable", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardPayable", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardCustomerCount", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardSafetyStockAlerts", xaml, StringComparison.Ordinal);
        Assert.Contains("ToggleDashboardSalesMetricsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardSalesMetricsExpanded", viewModel, StringComparison.Ordinal);
        Assert.Contains("DashboardSummaryColumnCount", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenDashboardReceivableDetailsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("OpenDashboardPayableDetailsCommand", xaml, StringComparison.Ordinal);
        Assert.Contains("DashboardBalanceDetailsWindow", viewModel, StringComparison.Ordinal);
        Assert.Contains("afterPaymentSavedAsync: LoadInvoiceListAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("ShowDashboardSalesMetricToggle", xaml, StringComparison.Ordinal);
        Assert.Contains("ShowDashboardExpandedSalesCards", xaml, StringComparison.Ordinal);
        Assert.Contains("CanViewDashboardSalesCards", xaml, StringComparison.Ordinal);
        Assert.Contains("public bool CanViewDashboardSalesCards => _session.HasAdministrativePrivileges;", viewModel, StringComparison.Ordinal);
        Assert.Contains("DashboardMonthlySales = 0m;", viewModel, StringComparison.Ordinal);
        Assert.Contains("DashboardMonthlyInvoiceCount = 0;", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("전월 대비", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardSalesTrendPercent", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardSalesTrendPercent", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardMonthlySalesChartPoints", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UpdateDashboardMonthlySalesChart", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("const int monthCount = 6", viewModel, StringComparison.Ordinal);
        Assert.Contains("invoice.VoucherType == VoucherType.Sales", viewModel, StringComparison.Ordinal);
        Assert.DoesNotContain("DashboardMonthlySalesChartPoint", viewModel, StringComparison.Ordinal);
        Assert.Contains("안전재고 알림", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("<Border Background=\"Transparent\" Margin=\"0,0,8,0\" CornerRadius=\"6\" Padding=\"10\"/>", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardBalanceDetailsWindow_ProvidesDirectPaymentProcessingPanel()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "DashboardBalanceDetailsWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "DashboardBalanceDetailViewModel.cs"));

        Assert.Contains("SelectedItem=\"{Binding SelectedRow, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"처리일\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectedDate=\"{Binding ProcessDate, Mode=TwoWay}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource UnifiedDatePickerButtonStyle}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"처리금액\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"메모\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsChecked=\"{Binding IsBatchSelected, Mode=TwoWay", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding FillCheckedBalanceAmountCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ProcessCheckedBalancesCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ProcessSelectedBalanceCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Command=\"{Binding ProcessSelectedFullBalanceCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SaveTransactionAsync(transaction, _session)", viewModel, StringComparison.Ordinal);
        Assert.Contains("SaveTransactionsAsync(transactions, _session)", viewModel, StringComparison.Ordinal);
        Assert.Contains("WaitForServerWriteWithTimeoutAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("await RefreshAsync();", viewModel, StringComparison.Ordinal);
        Assert.Contains("await _afterPaymentSavedAsync();", viewModel, StringComparison.Ordinal);
        Assert.Contains("MouseDoubleClick=\"BalanceRowsDataGrid_MouseDoubleClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("PreviewMouseRightButtonDown=\"BalanceRowsDataGrid_PreviewMouseRightButtonDown\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"전표 열기\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"전표 삭제\"", xaml, StringComparison.Ordinal);
        Assert.Contains("public long Revision { get; init; }", viewModel, StringComparison.Ordinal);
        Assert.Contains("Revision = invoice.Revision", File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "DashboardBalanceDetailBuilder.cs")), StringComparison.Ordinal);
        Assert.Contains("OpenInvoiceFromChildWindowAsync", File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs")), StringComparison.Ordinal);
        Assert.Contains("DeleteInvoiceFromChildWindowAsync", File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "MainWindow.xaml.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentWindow_KeepsInvoiceSettlementKindAndLongCustomerNameReadable()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Directory.EnumerateFiles(
                Path.Combine(root, "Desktop"),
                "PaymentWindow.xaml",
                SearchOption.AllDirectories)
            .Single();
        var localStatePath = Directory.EnumerateFiles(
                Path.Combine(root, "Desktop"),
                "LocalStateService.cs",
                SearchOption.AllDirectories)
            .Single();
        var syncServicePath = Directory.EnumerateFiles(
                Path.Combine(root, "Desktop"),
                "SyncService.cs",
                SearchOption.AllDirectories)
            .Single();

        var xaml = File.ReadAllText(xamlPath);
        var localState = File.ReadAllText(localStatePath);
        var syncService = File.ReadAllText(syncServicePath);

        Assert.Contains("ToolTip=\"{Binding CustomerName}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("TextTrimming=\"CharacterEllipsis\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MinWidth=\"260\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"수금구분\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"지급구분\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Text=\"처리방향\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("PaymentActionLabel", xaml, StringComparison.Ordinal);
        Assert.Contains("ResolveDirectPaymentTransactionKind(invoice)", localState, StringComparison.Ordinal);
        Assert.Contains("NormalizeLinkedPaymentNote(payment.Note, transactionKind)", localState, StringComparison.Ordinal);
        Assert.Contains("ResolvePulledPaymentTransactionKind(invoice)", syncService, StringComparison.Ordinal);
        Assert.Contains("NormalizeLinkedPaymentNote(payment.Note, transactionKind)", syncService, StringComparison.Ordinal);
    }

    [Fact]
    public void OfficeWorkflowWindows_DoNotRepeatSameReadOnlySummaryValueInOnePanel()
    {
        var root = FindRepositoryRoot();
        var periodLedgerXaml = File.ReadAllText(Directory.EnumerateFiles(
                Path.Combine(root, "Desktop"),
                "PeriodLedgerWindow.xaml",
                SearchOption.AllDirectories)
            .Single());
        var rentalOnboardingXaml = File.ReadAllText(Directory.EnumerateFiles(
                Path.Combine(root, "Desktop"),
                "RentalCustomerOnboardingWindow.xaml",
                SearchOption.AllDirectories)
            .Single());

        Assert.Equal(
            1,
            CountOccurrences(periodLedgerXaml, "SelectedCustomer.NameOriginal, TargetNullValue=선택된 거래처 없음"));
        Assert.Equal(
            1,
            CountOccurrences(rentalOnboardingXaml, "BillingStartDate, StringFormat=계약 체결일: {0:yyyy-MM-dd}"));
        Assert.Equal(
            2,
            CountOccurrences(rentalOnboardingXaml, "ExpectedBillingAmountText, StringFormat=예상 청구 금액: {0}"));
    }

    [Fact]
    public void DashboardBalanceDetailBuilder_GroupsReceivableRowsByCustomerAndKeepsInvoiceDetails()
    {
        var customerA = Guid.NewGuid();
        var customerB = Guid.NewGuid();
        var rows = DashboardBalanceDetailBuilder.BuildRows(
            [
                new LocalInvoiceListSummary
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerA,
                    InvoiceNumber = "S-001",
                    InvoiceDate = new DateOnly(2026, 7, 1),
                    VoucherType = VoucherType.Sales,
                    FirstItemSummary = "복합기 임대료",
                    TotalAmount = 100_000m,
                    SettledAmount = 40_000m,
                    ResponsibleOfficeCode = "USENET"
                },
                new LocalInvoiceListSummary
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerA,
                    InvoiceNumber = "S-002",
                    InvoiceDate = new DateOnly(2026, 7, 2),
                    VoucherType = VoucherType.Sales,
                    FirstItemSummary = "추가 장비",
                    TotalAmount = 30_000m,
                    SettledAmount = 0m,
                    ResponsibleOfficeCode = "USENET"
                },
                new LocalInvoiceListSummary
                {
                    Id = Guid.NewGuid(),
                    CustomerId = customerB,
                    InvoiceNumber = "P-001",
                    InvoiceDate = new DateOnly(2026, 7, 3),
                    VoucherType = VoucherType.Purchase,
                    FirstItemSummary = "매입 전표",
                    TotalAmount = 50_000m,
                    SettledAmount = 0m,
                    ResponsibleOfficeCode = "ITWORLD"
                }
            ],
            new Dictionary<Guid, string>
            {
                [customerA] = "테스트 거래처",
                [customerB] = "매입 거래처"
            },
            VoucherType.Sales);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal("테스트 거래처", row.CustomerName));
        Assert.All(rows, row => Assert.Equal(90_000m, row.CustomerBalance));
        Assert.Contains(rows, row => row.InvoiceNumberDisplay == "S-001" && row.BalanceAmount == 60_000m && row.FirstItemSummary == "복합기 임대료");
        Assert.Contains(rows, row => row.InvoiceNumberDisplay == "S-002" && row.BalanceAmount == 30_000m && row.FirstItemSummary == "추가 장비");
        Assert.DoesNotContain(rows, row => row.InvoiceNumberDisplay == "P-001");
    }

    [Fact]
    public void PeriodLedgerWindow_ShowsReportSummaryBalanceAndCollectionRate()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "PeriodLedgerWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "PeriodLedgerViewModel.cs"));
        var service = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Services",
            "PeriodLedgerAggregationService.cs"));

        Assert.Contains("Text=\"요약\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"미수잔액\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"수금율\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SummaryReceivableBalanceText", xaml, StringComparison.Ordinal);
        Assert.Contains("SummaryCollectionRateText", xaml, StringComparison.Ordinal);
        Assert.Contains("TabControl", xaml, StringComparison.Ordinal);
        Assert.Contains("Header=\"월별 매출 차트\"", xaml, StringComparison.Ordinal);
        Assert.Contains("MonthlySalesChartPoints", xaml, StringComparison.Ordinal);
        Assert.Contains("SalesAmountText", xaml, StringComparison.Ordinal);
        Assert.Contains("BarHeight", xaml, StringComparison.Ordinal);
        Assert.Contains("조회 월 수 제한 없이", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Visibility=\"Collapsed\"", ExtractPeriodLedgerSummaryBlock(xaml), StringComparison.Ordinal);
        Assert.Contains("FormatCollectionRate", viewModel, StringComparison.Ordinal);
        Assert.Contains("MonthlySalesChartSummaryText", viewModel, StringComparison.Ordinal);
        Assert.Contains("ObservableCollection<PeriodLedgerMonthlySalesChartPoint>", viewModel, StringComparison.Ordinal);
        Assert.Contains("totals.ReceiptAmount + Math.Max(0m, totals.ReceivableBalance)", viewModel, StringComparison.Ordinal);
        Assert.Contains("BuildMonthlySalesChartPoints", service, StringComparison.Ordinal);
        Assert.DoesNotContain("Take(", service, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryViewReferencedInteractionHandler_ExistsInCodeBehind()
    {
        var root = FindRepositoryRoot();
        var viewRoot = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views");
        var failures = new List<string>();
        var eventPattern = new Regex(
            "\\b(?:Click|MouseDoubleClick|KeyDown|TextChanged|SelectionChanged|Checked|Unchecked|Loaded|Closing)=\"(?<handler>[A-Za-z_][A-Za-z0-9_]*)\"",
            RegexOptions.CultureInvariant);

        foreach (var xamlPath in Directory.EnumerateFiles(viewRoot, "*.xaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(xamlPath);
            var codePath = xamlPath + ".cs";
            var code = File.Exists(codePath) ? File.ReadAllText(codePath) : string.Empty;
            foreach (Match match in eventPattern.Matches(xaml))
            {
                var handlerName = match.Groups["handler"].Value;
                if (code.Contains(handlerName, StringComparison.Ordinal))
                    continue;

                failures.Add($"{RelativeToRoot(root, xamlPath)}:{GetLineNumber(xaml, match.Index)} '{handlerName}' handler가 code-behind에 없습니다.");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    [Fact]
    public void ImmediateActionSelectionCheckboxes_UpdateSourceOnFirstClick()
    {
        var root = FindRepositoryRoot();

        AssertImmediateSelectionCheckbox(
            root,
            "InvoiceHistoryWindow.xaml",
            "ConfirmButton_Click");
        AssertImmediateSelectionCheckbox(
            root,
            "RentalCustomerOnboardingWindow.xaml",
            "ApplySelectedAssetsToTemplateCommand");
    }

    [Fact]
    public void DataGridCheckBoxColumns_ToggleWithSingleClickAndKeepStatusColumnsReadOnly()
    {
        var root = FindRepositoryRoot();
        var appStartup = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "App.xaml.cs"));
        var service = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Infrastructure",
            "DataGridCheckBoxSingleClickService.cs"));

        Assert.Contains("DataGridCheckBoxSingleClickService.RegisterGlobal();", appStartup, StringComparison.Ordinal);
        Assert.Contains("EventManager.RegisterClassHandler", service, StringComparison.Ordinal);
        Assert.Contains("UIElement.PreviewMouseLeftButtonDownEvent", service, StringComparison.Ordinal);
        Assert.Contains("DataGridCheckBoxColumn checkBoxColumn", service, StringComparison.Ordinal);
        Assert.Contains("TryToggleBoundBoolean", service, StringComparison.Ordinal);
        Assert.Contains("binding.Mode is BindingMode.OneWay or BindingMode.OneTime", service, StringComparison.Ordinal);
        Assert.Contains("propertyDescriptor.IsReadOnly", service, StringComparison.Ordinal);
        Assert.Contains("e.Handled = true", service, StringComparison.Ordinal);

        AssertReadOnlyCheckBoxColumn(root, "CustomerManagementWindow.xaml", "Header=\"변경됨\"");
        AssertReadOnlyCheckBoxColumn(root, "RentalAssetWindow.xaml", "Header=\"이상\"");
        AssertReadOnlyCheckBoxColumn(root, "RentalSettingsWindow.xaml", "Header=\"자동\"");
        AssertReadOnlyCheckBoxColumn(root, "SyncDiagnosticsWindow.xaml", "Header=\"복구\"");
    }

    [Fact]
    public void OfficeCodeCatalog_KeepsItworldVisibleAsKoreanNameWithCode()
    {
        Assert.Equal("아이티월드[ITWORLD]", OfficeCodeCatalog.GetOfficeDisplayName(OfficeCodeCatalog.Itworld));
        Assert.True(OfficeCodeCatalog.TryNormalizeOfficeCode("아이티월드", out var normalizedOfficeCode));
        Assert.Equal(OfficeCodeCatalog.Itworld, normalizedOfficeCode);
    }

    [Fact]
    public void RentalAssetWindow_KeepsDetailSelectionSingleAndSelectionAutosaveStable()
    {
        var root = FindRepositoryRoot();
        var xaml = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "Views",
            "RentalAssetWindow.xaml"));
        var viewModel = File.ReadAllText(Path.Combine(
            root,
            "Desktop",
            "거래플랜.Desktop.App",
            "ViewModels",
            "RentalAssetViewModel.cs"));

        Assert.Contains("ItemsSource=\"{Binding Rows}\" SelectedItem=\"{Binding SelectedRow}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionMode=\"Single\"", xaml, StringComparison.Ordinal);
        Assert.Contains("SelectionUnit=\"FullRow\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SelectionMode=\"Extended\"", xaml, StringComparison.Ordinal);
        Assert.Contains("private async Task<bool> HandleSelectionAutoSaveAsync", viewModel, StringComparison.Ordinal);
        Assert.Contains("preserveSelectionRowId: requestedSelection?.Source.Id,", viewModel, StringComparison.Ordinal);
        Assert.Contains("refreshAfterSave: false,", viewModel, StringComparison.Ordinal);
        Assert.Contains("RefreshSavedAssetRowInPlaceAsync(savedAssetId, preserveSelectionRowId)", viewModel, StringComparison.Ordinal);
        Assert.Contains(
            "fullRow.IsSelected = current?.IsSelected ?? fullRow.IsSelected;",
            viewModel,
            StringComparison.Ordinal);
    }

    [Fact]
    public void PaymentTransferVerifier_CapturesRuntimeWindowScreenshotsAndDatePickerMetrics()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "tasks",
            "PaymentTransferVerifier",
            "Program.cs"));

        Assert.Contains("RenderTargetBitmap", source, StringComparison.Ordinal);
        Assert.Contains("PngBitmapEncoder", source, StringComparison.Ordinal);
        Assert.Contains("CaptureWindow(paymentAdvanceWindow", source, StringComparison.Ordinal);
        Assert.Contains("CollectDatePickerMetrics(paymentAdvanceWindow", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDatePickerMetrics(datePickerMetrics)", source, StringComparison.Ordinal);
        Assert.Contains("DatePickerRuntimeMetric", source, StringComparison.Ordinal);
        Assert.Contains("WindowScreenshot", source, StringComparison.Ordinal);
    }

    [Fact]
    public void WpfDatePickerRuntimeAudit_CapturesRemainingDatePickerWindows()
    {
        var root = FindRepositoryRoot();
        var source = File.ReadAllText(Path.Combine(
            root,
            "tasks",
            "WpfDatePickerRuntimeAudit",
            "Program.cs"));

        Assert.Contains("RequiredWindowDatePickerCounts", source, StringComparison.Ordinal);
        Assert.Contains("[\"customer-edit\"] = 3", source, StringComparison.Ordinal);
        Assert.Contains("[\"inventory-transfer\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"period-ledger\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("[\"print-edit\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-asset-link\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-asset\"] = 5", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-assignment-history-edit\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-billing\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-contract-editor\"] = 3", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-customer-onboarding\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"rental-equipment-replacement\"] = 1", source, StringComparison.Ordinal);
        Assert.Contains("[\"yeonsu-delivery\"] = 2", source, StringComparison.Ordinal);
        Assert.Contains("RenderTargetBitmap", source, StringComparison.Ordinal);
        Assert.Contains("PngBitmapEncoder", source, StringComparison.Ordinal);
        Assert.Contains("ValidateDatePickerMetrics(datePickerMetrics)", source, StringComparison.Ordinal);
        Assert.Contains("ComplementsExistingPaymentTransferVerifier = true", source, StringComparison.Ordinal);
    }

    private static bool TryReadSetterInt(string styleBlock, string propertyName, out int value)
    {
        var match = Regex.Match(
            styleBlock,
            $"Property=\"{Regex.Escape(propertyName)}\"\\s+Value=\"(?<value>\\d+)\"",
            RegexOptions.CultureInvariant);

        if (match.Success && int.TryParse(match.Groups["value"].Value, out value))
            return true;

        value = 0;
        return false;
    }

    private static int CountOccurrences(string source, string value)
        => Regex.Matches(source, Regex.Escape(value), RegexOptions.CultureInvariant).Count;

    private static string ExtractBlock(string source, string startMarker, string endMarker)
    {
        var normalizedSource = source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var start = normalizedSource.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"시작 마커를 찾을 수 없습니다: {startMarker}");

        var end = normalizedSource.IndexOf(endMarker, start, StringComparison.Ordinal);
        Assert.True(end > start, $"끝 마커를 찾을 수 없습니다: {endMarker}");

        return normalizedSource[start..end];
    }

    private static string ExtractPeriodLedgerSummaryBlock(string source)
        => ExtractBlock(
            source,
            "<TextBlock Grid.Row=\"0\" Grid.ColumnSpan=\"2\"\n                                       Text=\"요약\"",
            "<TextBlock Grid.Row=\"3\"\n                               Text=\"화면은 거래 1건을 1줄로 보여주고");

    private static void AssertImmediateSelectionCheckbox(string root, string viewName, string actionMarker)
    {
        var xamlPath = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views", viewName);
        var xaml = File.ReadAllText(xamlPath);

        Assert.DoesNotContain("DataGridCheckBoxColumn Header=\"선택\" Binding=\"{Binding IsSelected", xaml, StringComparison.Ordinal);
        Assert.Contains("<DataGridTemplateColumn Header=\"선택\"", xaml, StringComparison.Ordinal);
        Assert.Contains(
            "IsChecked=\"{Binding IsSelected, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}\"",
            xaml,
            StringComparison.Ordinal);
        Assert.Contains(actionMarker, xaml, StringComparison.Ordinal);
    }

    private static void AssertReadOnlyCheckBoxColumn(string root, string viewName, string headerMarker)
    {
        var xamlPath = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views", viewName);
        var xaml = File.ReadAllText(xamlPath);
        var headerIndex = xaml.IndexOf(headerMarker, StringComparison.Ordinal);
        Assert.True(headerIndex >= 0, $"{viewName}에서 {headerMarker} 체크박스 컬럼을 찾을 수 없습니다.");

        var columnStart = xaml.LastIndexOf("<DataGridCheckBoxColumn", headerIndex, StringComparison.Ordinal);
        Assert.True(columnStart >= 0, $"{viewName}에서 {headerMarker} 체크박스 컬럼 시작을 찾을 수 없습니다.");

        var columnEnd = xaml.IndexOf('>', headerIndex);
        Assert.True(columnEnd > headerIndex, $"{viewName}에서 {headerMarker} 체크박스 컬럼 끝을 찾을 수 없습니다.");

        var columnTag = xaml[columnStart..(columnEnd + 1)];
        Assert.Contains("IsReadOnly=\"True\"", columnTag, StringComparison.Ordinal);
    }

    [Fact]
    public void SalesWindow_RentalLinkedInvoice_ShowsSaveActionAndEditBoundaryNotice()
    {
        var root = FindRepositoryRoot();
        var xamlPath = Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views", "SalesWindow.xaml");
        var xaml = File.ReadAllText(xamlPath);

        Assert.Contains("Command=\"{Binding SaveCommand}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("IsRentalBillingLinkedInvoice", xaml, StringComparison.Ordinal);
        Assert.Contains("RentalBillingLinkedNoticeText", xaml, StringComparison.Ordinal);
        Assert.Contains("RentalBillingLinkedReferenceText", xaml, StringComparison.Ordinal);
        Assert.Contains("렌탈 청구 전표", xaml, StringComparison.Ordinal);
    }

    private static int GetLineNumber(string source, int index)
        => source[..index].Count(ch => ch == '\n') + 1;

    private static string RelativeToRoot(string root, string path)
        => Path.GetRelativePath(root, path);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "거래플랜.sln")))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("거래플랜.sln을 찾을 수 없습니다.");
    }
}
