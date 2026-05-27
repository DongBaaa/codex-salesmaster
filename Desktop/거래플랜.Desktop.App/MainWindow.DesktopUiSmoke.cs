using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Desktop.App.Views;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App;

public partial class MainWindow
{
    public void QueueDesktopUiSmokeSelfTestIfRequested()
    {
        var reportPath = Environment.GetEnvironmentVariable("GEORAEPLAN_DESKTOP_UI_SMOKE_REPORT");
        if (string.IsNullOrWhiteSpace(reportPath))
            return;

        UiTaskHelper.Forget(
            RunDesktopUiSmokeSelfTestAsync(reportPath),
            "UI-SMOKE",
            "데스크톱 UI 자체 검증",
            ex => AppLogger.Error("UI-SMOKE", "데스크톱 UI 자체 검증 실패", ex));
    }

    private async Task RunDesktopUiSmokeSelfTestAsync(string reportPath)
    {
        var steps = new List<DesktopUiSmokeSelfTestStep>();

        await Task.Delay(1000);

        await VerifySmokeWindowAsync(
            steps,
            "거래처 관리",
            async () =>
            {
                var vm = new CustomerManagementViewModel(_local, _session);
                await vm.InitializeAsync();
                return new CustomerManagementWindow(vm, _local, _session) { Owner = this };
            },
            "새 거래처 등록",
            "선택 거래처 수정",
            "선택 거래처 삭제",
            "담당지점 저장");

        await VerifySmokeWindowAsync(
            steps,
            "품목/재고 관리",
            async () =>
            {
                var vm = new InventoryViewModel(_local, _session);
                await vm.LoadAsync();
                return new InventoryWindow(vm) { Owner = this };
            },
            "신규 품목",
            "품목 저장",
            "재고 초기화",
            "닫기 (F12)");

        await VerifySmokeWindowAsync(
            steps,
            "판매(매출)",
            async () =>
            {
                var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, VoucherType.Sales);
                await vm.LoadAsync();
                vm.NewInvoice();
                return new SalesWindow(vm) { Owner = this };
            },
            "판매(매출)",
            "수금 입력",
            "항목추가");

        await VerifySmokeWindowAsync(
            steps,
            "구매(매입)",
            async () =>
            {
                var vm = new SalesViewModel(_local, _print, _invoicePrintService, _session, VoucherType.Purchase);
                await vm.LoadAsync();
                vm.NewInvoice();
                return new SalesWindow(vm) { Owner = this };
            },
            "구매(매입)",
            "지급 입력",
            "항목추가");

        var passed = steps.All(step => step.Passed);
        var payload = new
        {
            CreatedAt = DateTimeOffset.Now,
            Result = passed ? "PASS" : "FAIL",
            Steps = steps
        };

        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var jsonPath = Path.ChangeExtension(reportPath, ".json");
        await File.WriteAllTextAsync(
            jsonPath,
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        await File.WriteAllLinesAsync(
            reportPath,
            BuildDesktopUiSmokeSelfTestMarkdown(payload.Result, steps, jsonPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        AppLogger.Info("UI-SMOKE", $"데스크톱 UI 자체 검증 완료: {payload.Result}, report={reportPath}");
    }

    private async Task VerifySmokeWindowAsync(
        ICollection<DesktopUiSmokeSelfTestStep> steps,
        string name,
        Func<Task<Window>> createWindowAsync,
        params string[] requiredTexts)
    {
        Window? window = null;
        try
        {
            window = await createWindowAsync();
            window.ShowInTaskbar = false;
            window.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            await Task.Delay(250);

            var texts = CollectVisibleText(window);
            var missing = requiredTexts
                .Where(required => !texts.Contains(required, StringComparer.OrdinalIgnoreCase))
                .ToArray();

            steps.Add(new DesktopUiSmokeSelfTestStep(
                name,
                missing.Length == 0,
                missing.Length == 0
                    ? $"창 생성/표시 및 필수 표시값 확인: {string.Join(", ", requiredTexts)}"
                    : $"누락 표시값: {string.Join(", ", missing)} / 수집={string.Join(", ", texts.Take(40))}"));
        }
        catch (Exception ex)
        {
            steps.Add(new DesktopUiSmokeSelfTestStep(name, false, ex.Message));
        }
        finally
        {
            if (window is not null)
                CloseWindowForSmoke(window);
        }
    }

    private static IReadOnlyCollection<string> CollectVisibleText(DependencyObject root)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectVisibleTextCore(root, values);
        return values;
    }

    private static void CollectVisibleTextCore(DependencyObject element, ISet<string> values)
    {
        switch (element)
        {
            case TextBlock textBlock:
                AddText(values, textBlock.Text);
                break;
            case HeaderedContentControl headeredContentControl:
                AddText(values, headeredContentControl.Header);
                AddText(values, headeredContentControl.Content);
                break;
            case ContentControl contentControl:
                AddText(values, contentControl.Content);
                break;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(element);
        for (var i = 0; i < childCount; i++)
            CollectVisibleTextCore(VisualTreeHelper.GetChild(element, i), values);
    }

    private static void AddText(ISet<string> values, object? value)
    {
        if (value is string text && !string.IsNullOrWhiteSpace(text))
            values.Add(text.Trim());
    }

    private static void CloseWindowForSmoke(Window window)
    {
        foreach (var fieldName in new[] { "_allowCloseWithoutSave", "_closeInProgress" })
        {
            var field = window.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field?.FieldType == typeof(bool))
                field.SetValue(window, fieldName == "_allowCloseWithoutSave");
        }

        if (window.IsLoaded)
            window.Close();
    }

    private static IEnumerable<string> BuildDesktopUiSmokeSelfTestMarkdown(
        string result,
        IReadOnlyCollection<DesktopUiSmokeSelfTestStep> steps,
        string jsonPath)
    {
        yield return "# 거래플랜 Desktop UI 자체 검증";
        yield return "";
        yield return $"- 작성시각: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        yield return $"- 결과: **{result}**";
        yield return "";
        yield return "| 창 | 결과 | 상세 |";
        yield return "|---|---|---|";
        foreach (var step in steps)
            yield return $"| {step.Name} | {(step.Passed ? "PASS" : "FAIL")} | {step.Detail.Replace("|", "\\|")} |";
        yield return "";
        yield return $"JSON: {jsonPath}";
    }

    private sealed record DesktopUiSmokeSelfTestStep(string Name, bool Passed, string Detail);
}
