using System.Collections;
using System.Reflection;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Printing;
using 거래플랜.Desktop.App.Services;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class InvoicePrintLineOrderTests
{
    [Fact]
    public void InvoicePrintLineSynchronizer_AlignsSavedLinesToCurrentInvoiceOrder()
    {
        var firstSourceId = Guid.Parse("11111111-aaaa-1111-aaaa-111111111111");
        var secondSourceId = Guid.Parse("22222222-bbbb-2222-bbbb-222222222222");
        var saved = new List<InvoicePrintLineModel>
        {
            new()
            {
                SourceLineId = secondSourceId,
                No = 20,
                ItemName = "B",
                Specification = "saved second"
            },
            new()
            {
                SourceLineId = firstSourceId,
                No = 10,
                ItemName = "A",
                Specification = "saved first"
            }
        };
        var current = new List<InvoicePrintLineModel>
        {
            new() { SourceLineId = firstSourceId, No = 1, ItemName = "A" },
            new() { SourceLineId = secondSourceId, No = 2, ItemName = "B" }
        };

        var aligned = InvoicePrintLineSynchronizer.AlignToInvoiceLineOrder(saved, current);

        Assert.Collection(
            aligned,
            line =>
            {
                Assert.Equal(1, line.No);
                Assert.Equal("A", line.ItemName);
                Assert.Equal("saved first", line.Specification);
            },
            line =>
            {
                Assert.Equal(2, line.No);
                Assert.Equal("B", line.ItemName);
                Assert.Equal("saved second", line.Specification);
            });
    }

    [Fact]
    public void WpfInvoicePrintService_NormalizeLinesKeepsListOrderInsteadOfStaleNo()
    {
        var normalized = InvokePrivateStatic<List<InvoicePrintLineModel>>(
            typeof(WpfInvoicePrintService),
            "NormalizeLines",
            new List<InvoicePrintLineModel>
            {
                new() { No = 99, ItemName = "first" },
                new() { No = 1, ItemName = "second" }
            });

        Assert.Collection(
            normalized,
            line =>
            {
                Assert.Equal(1, line.No);
                Assert.Equal("first", line.ItemName);
            },
            line =>
            {
                Assert.Equal(2, line.No);
                Assert.Equal("second", line.ItemName);
            });
    }

    [Fact]
    public void SupplementDocumentBuilder_ResolveEstimateLinesKeepsPrintModelListOrder()
    {
        var result = InvokePrivateStatic<object>(
            typeof(SupplementDocumentBuilder),
            "ResolveEstimateLines",
            new LocalInvoice(),
            new InvoicePrintModel
            {
                Lines =
                [
                    new InvoicePrintLineModel { No = 99, ItemName = "first" },
                    new InvoicePrintLineModel { No = 1, ItemName = "second" }
                ]
            });

        var items = Assert.IsAssignableFrom<IEnumerable>(result).Cast<object>().ToList();
        Assert.Equal(new[] { "first", "second" }, items.Select(ReadItemName).ToArray());
        Assert.Equal(new[] { 1, 2 }, items.Select(ReadNo).ToArray());
    }

    private static string ReadItemName(object target)
        => Assert.IsType<string>(target.GetType().GetProperty("ItemName", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target));

    private static int ReadNo(object target)
        => Assert.IsType<int>(target.GetType().GetProperty("No", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target));

    private static T InvokePrivateStatic<T>(Type type, string methodName, params object?[] args)
    {
        var method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (T)method!.Invoke(null, args)!;
    }
}
