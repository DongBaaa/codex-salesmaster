using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class LedgerCostConfidenceBindingTests
{
    [Fact]
    public void ActualLedgerBindingsShowProvisionalAmountsAndClearSummaryLabels()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VerifyBindings(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(20)), "WPF binding verification timed out.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void VerifyBindings([CallerFilePath] string sourcePath = "")
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", ".."));
        var document = XDocument.Load(Path.Combine(root, "Desktop", "거래플랜.Desktop.App", "Views", "YeonsuDeliveryWindow.xaml"));
        XNamespace w = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        using var vm = new YeonsuDeliveryViewModel(null!, new SessionState());
        var labels = new List<TextBlock>();
        foreach (var property in new[] { "SummaryPurchaseLabel", "SummaryProfitLabel", "SummaryFeeLabel" })
        {
            var original = document.Descendants(w + "TextBlock").Single(e => (string?)e.Attribute("Text") == "{Binding " + property + "}");
            var label = (TextBlock)XamlReader.Parse(new XElement(w + "TextBlock", original.Attribute("Text")!).ToString());
            label.DataContext = vm;
            labels.Add(label);
        }
        vm.UncertainCostRowCount = 1;
        Drain();
        Assert.All(labels, label => Assert.Contains("잠정", label.Text));
        vm.UncertainCostRowCount = 0;
        Drain();
        Assert.All(labels, label => Assert.DoesNotContain("잠정", label.Text));

        var row = new YeonsuDeliveryRow { PurchaseAmount = 20, ProfitAmount = 100, FeeAmount = 20, IsCostUncertain = true };
        foreach (var property in new[] { "PurchaseAmount", "ProfitAmount", "FeeAmount" })
        {
            var original = document.Descendants(w + "DataGridTextColumn").Single(e => (string?)e.Attribute("SortMemberPath") == property);
            var column = (DataGridTextColumn)XamlReader.Parse(new XElement(w + "DataGridTextColumn", original.Attribute("Binding")!, original.Attribute("SortMemberPath")!).ToString());
            Assert.Equal(property, column.SortMemberPath);
            var text = new TextBlock { DataContext = row };
            BindingOperations.SetBinding(text, TextBlock.TextProperty, column.Binding);
            Drain();
            Assert.Contains("잠정", text.Text);
            text.DataContext = new YeonsuDeliveryRow { PurchaseAmount = 0, ProfitAmount = 100, FeeAmount = 20 };
            Drain();
            Assert.DoesNotContain("잠정", text.Text);
        }
    }

    private static void Drain() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
}
