using System.Xml.Linq;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class ServerIntegrityResolutionAndPlainLanguageTests
{
    [Theory]
    [InlineData("렌탈청구품목", DataIntegrityDirectActionKind.OpenRentalBillingProfile)]
    [InlineData("렌탈 청구 run", DataIntegrityDirectActionKind.OpenRentalBillingProfile)]
    [InlineData("렌탈청구프로필", DataIntegrityDirectActionKind.OpenRentalBillingProfile)]
    [InlineData("렌탈자산", DataIntegrityDirectActionKind.OpenRentalAsset)]
    [InlineData("품목", DataIntegrityDirectActionKind.OpenInventoryItem)]
    [InlineData("거래처", DataIntegrityDirectActionKind.OpenCustomer)]
    [InlineData("전표", DataIntegrityDirectActionKind.OpenInvoice)]
    public void ResolutionPlan_MapsOnlyKnownEditableTargets(
        string entityType,
        DataIntegrityDirectActionKind expectedAction)
    {
        var targetId = Guid.NewGuid();
        var plan = ServerIntegrityResolutionPlan.Create(
            BuildIssue("rental_asset_template_monthly_mismatch", "월요금이 다릅니다."),
            new IntegrityIssueDetailRowDto
            {
                EntityType = entityType,
                EntityIdText = targetId.ToString("D")
            });

        Assert.True(plan.CanOpenTarget);
        Assert.Equal(expectedAction, plan.DirectActionKind);
        Assert.Equal(targetId, plan.TargetEntityId);
        Assert.Contains("실제 계약", plan.ProblemExplanation);
        Assert.Contains("렌탈 청구관리", plan.SuggestedAction);
    }

    [Theory]
    [InlineData("과거 렌탈 임대이력", "c3c5971d-9128-44e4-939c-909688bf2c9e")]
    [InlineData("렌탈자산", "잘못된-ID")]
    [InlineData("재고이동", "c3c5971d-9128-44e4-939c-909688bf2c9e")]
    public void ResolutionPlan_DoesNotGuessAmbiguousOrInvalidTargets(
        string entityType,
        string entityIdText)
    {
        var plan = ServerIntegrityResolutionPlan.Create(
            BuildIssue("rental_assignment_historical_stale_reference_rows", "과거 참조"),
            new IntegrityIssueDetailRowDto
            {
                EntityType = entityType,
                EntityIdText = entityIdText
            });

        Assert.False(plan.CanOpenTarget);
        Assert.Equal(DataIntegrityDirectActionKind.None, plan.DirectActionKind);
        Assert.Null(plan.TargetEntityId);
    }

    [Theory]
    [InlineData("rental_billing_manual_stop_status_mismatch", "청구를 계속할지")]
    [InlineData("rental_profile_customer_unlinked", "실제 거래처")]
    [InlineData("duplicate_item_name_match_keys", "재고·전표")]
    [InlineData("rental_assignment_historical_stale_reference_rows", "현재 청구에 영향이 없는")]
    public void ServerGuidance_ExplainsConcreteUserDecision(string code, string expectedText)
    {
        var guidance = IntegrityIssueGuidance.GetSuggestedAction(code, "기술 메시지");

        Assert.Contains(expectedText, guidance);
    }

    [Fact]
    public void OperationalAndSyncMessages_PutPlainExplanationBeforeTechnicalDetail()
    {
        var operational = new DataIntegrityIssueDetail
        {
            Code = DataIntegrityIssueCodes.RentalAssetBillingEligibilityUnconfirmed,
            Title = "청구상태 확인 필요",
            Severity = "Warning",
            Message = "BillingProfileId 없음 / 템플릿 참조 없음",
            SuggestedAction = "자산 화면에서 청구 대상을 선택하세요.",
            DirectActionKind = DataIntegrityDirectActionKind.OpenRentalAsset
        };
        var sync = new SyncDiagnosticListItem
        {
            Category = "저장/동기화 확인",
            Subcategory = "remaining_dirty",
            RawMessage = "동기화 후 dirty 잔존",
            IsRecoverable = true
        };

        Assert.Contains("매월 청구해야 하는지", operational.ProblemExplanation);
        Assert.Contains("해당 화면에서 수정", operational.ActionSteps);
        Assert.Equal(operational.Message, operational.TechnicalDetailText);
        Assert.Contains("서버의 최종 확인", sync.ProblemExplanation);
        Assert.Contains("선택 항목 복구", sync.ActionSteps);
        Assert.DoesNotContain("dirty", sync.ProblemExplanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SyncDiagnosticsXaml_HidesCodeAndWiresDetailDoubleClick()
    {
        var path = Path.Combine(FindDesktopAppDirectory(), "Views", "SyncDiagnosticsWindow.xaml");
        var document = XDocument.Load(path);
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var issueGrid = Assert.Single(
            document.Descendants(),
            element => element.Name.LocalName == "DataGrid" &&
                       ((string?)element.Attribute("ItemsSource"))?.Contains("ServerIntegrityIssues", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(
            issueGrid.Descendants(),
            column => string.Equals((string?)column.Attribute("Header"), "코드", StringComparison.Ordinal));

        var detailGrid = Assert.Single(
            document.Descendants(),
            element => string.Equals(
                (string?)element.Attribute(xaml + "Name"),
                "ServerIntegrityDetailDataGrid",
                StringComparison.Ordinal));
        Assert.Equal("ServerIntegrityDetailDataGrid_MouseDoubleClick", (string?)detailGrid.Attribute("MouseDoubleClick"));
        Assert.Contains("SelectedServerIntegrityDetailRow", (string?)detailGrid.Attribute("SelectedItem"));
    }

    [Fact]
    public void ResolutionWindow_KeepsActionsVisibleAndHorizontalOverflowDisabled()
    {
        var path = Path.Combine(FindDesktopAppDirectory(), "Views", "ServerIntegrityResolutionWindow.xaml");
        var document = XDocument.Load(path);
        var window = Assert.IsType<XElement>(document.Root);
        Assert.Equal("720", (string?)window.Attribute("MinWidth"));
        Assert.Equal("560", (string?)window.Attribute("MinHeight"));

        var scrollViewer = Assert.Single(document.Descendants(), element => element.Name.LocalName == "ScrollViewer");
        Assert.Equal("Disabled", (string?)scrollViewer.Attribute("HorizontalScrollBarVisibility"));
        Assert.Equal("Auto", (string?)scrollViewer.Attribute("VerticalScrollBarVisibility"));
        Assert.DoesNotContain(document.Descendants(), element =>
            ((string?)element.Attribute("Width"))?.Contains("minmax", StringComparison.OrdinalIgnoreCase) == true);

        var buttons = document.Descendants().Where(element => element.Name.LocalName == "Button").ToArray();
        Assert.Contains(buttons, button => ((string?)button.Attribute("Content"))?.Contains("ActionButtonText", StringComparison.Ordinal) == true);
        Assert.Contains(buttons, button => string.Equals((string?)button.Attribute("Content"), "수정 후 다시 검사", StringComparison.Ordinal));
        Assert.Contains(buttons, button => string.Equals((string?)button.Attribute("Content"), "닫기(F12)", StringComparison.Ordinal));
    }

    [Fact]
    public void MainWindow_WiresResolutionTargetsToExistingEditorsAndRecheck()
    {
        var path = Path.Combine(FindDesktopAppDirectory(), "MainWindow.xaml.cs");
        var source = File.ReadAllText(path);

        Assert.Contains("window.ResolutionTargetRequested +=", source, StringComparison.Ordinal);
        Assert.Contains("OpenServerIntegrityResolutionTargetAsync", source, StringComparison.Ordinal);
        Assert.Contains("RecheckServerIntegrityIssueAsync(args.IssueCode)", source, StringComparison.Ordinal);
        Assert.Contains("OpenRentalBillingWindowAsync(args.TargetEntityId", source, StringComparison.Ordinal);
        Assert.Contains("OpenRentalAssetWindowAsync(args.TargetEntityId", source, StringComparison.Ordinal);
        Assert.Contains("OpenInventoryWindowAsync(args.TargetEntityId", source, StringComparison.Ordinal);
        Assert.Contains("OpenCustomerEditorAsync(args.TargetEntityId", source, StringComparison.Ordinal);
        Assert.Contains("OpenInvoiceWindowAsync(args.TargetEntityId", source, StringComparison.Ordinal);
    }

    private static IntegrityIssueDto BuildIssue(string code, string message)
        => new()
        {
            Code = code,
            Severity = "Warning",
            Count = 1,
            Message = message
        };

    private static string FindDesktopAppDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var desktop = Path.Combine(current.FullName, "Desktop");
            var tests = Path.Combine(current.FullName, "Tests");
            if (Directory.Exists(desktop) && Directory.Exists(tests))
                return Directory.GetDirectories(desktop, "*.Desktop.App").Single();

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root not found.");
    }
}
