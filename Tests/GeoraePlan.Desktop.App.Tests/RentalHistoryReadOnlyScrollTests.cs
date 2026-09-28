using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalHistoryReadOnlyScrollTests
{
    [Theory]
    [InlineData("USENET")]
    [InlineData("ITWORLD")]
    [InlineData("YEONSU")]
    public Task ReadOnlyStaffCanScrollHistoryWithoutEnablingEdits(string assetOffice) => OnSta(async () =>
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();
        var session = new SessionState();
        session.SetOfflineSession(new UserSessionDto { Username="history-reader", Role="User", OfficeCode="USENET", TenantCode="USENET_GROUP", ScopeType="OfficeOnly" });
        var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
        var rental = new RentalStateService(db, local);
        var vm = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
        vm.SelectedRow = new RentalAssetViewRow { Source = new LocalRentalAsset { Id=Guid.Empty, OfficeCode=assetOffice, ResponsibleOfficeCode=assetOffice, ManagementCompanyCode=assetOffice, TenantCode=assetOffice=="ITWORLD" ? "ITWORLD" : "USENET_GROUP" } };
        vm.AssignmentHistories.Add(new RentalAssetAssignmentHistoryViewItem { MonthlyFee=null, ChangeReason="read-only-test" });
        var view = ReadActualHistoryLayout();
        view.DataContext = vm;
        try
        {
            view.Measure(new Size(520, 400)); view.Arrange(new Rect(0,0,520,400)); view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var grid = Assert.IsType<DataGrid>(view.FindName("AssignmentHistoryGrid"));
            Assert.True(grid.IsEnabled);
            Assert.True(grid.IsReadOnly);
            Assert.False(Find<TextBox>(view).Single().IsEnabled);
            grid.SelectedIndex=0;
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Same(vm.AssignmentHistories[0],vm.SelectedAssignmentHistory);
            Assert.False(vm.AddAssignmentHistoryCommand.CanExecute(null));
            Assert.False(vm.EditAssignmentHistoryCommand.CanExecute(null));
            Assert.False(vm.DeleteAssignmentHistoryCommand.CanExecute(null));
            Assert.False(vm.SaveCommand.CanExecute(null));
            var scroll=Find<ScrollViewer>(grid).First();
            Assert.True(scroll.IsEnabled);
            Assert.True(scroll.ScrollableWidth>0);
            scroll.ScrollToRightEnd(); view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.True(scroll.HorizontalOffset>0);
            var fee=grid.Columns.Single(c=>Equals(c.Header,"월요금"));
            grid.ScrollIntoView(vm.AssignmentHistories[0],fee); view.UpdateLayout();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert.Equal("비공개",Assert.IsType<TextBlock>(fee.GetCellContent(vm.AssignmentHistories[0])).Text);
            Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
            Assert.False(vm.HasPendingChanges);
        }
        finally { view.DataContext=null; await vm.CancelPendingBackgroundWorkAsync(); }
    });

    private static FrameworkElement ReadActualHistoryLayout()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !Directory.Exists(Path.Combine(root.FullName,"Desktop"))) root=root.Parent;
        Assert.NotNull(root);
        var doc=XDocument.Load(Path.Combine(root.FullName,"Desktop","거래플랜.Desktop.App","Views","RentalAssetWindow.xaml"));
        XNamespace x="http://schemas.microsoft.com/winfx/2006/xaml";
        var layout=new XElement(doc.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="RentalAssetDetailViewportContent"));
        var history=layout.Descendants().Single(e=>(string?)e.Attribute(x+"Name")=="AssignmentHistoryGrid");
        var editor=layout.Descendants().First(e=>e.Name.LocalName=="TextBox" && ((string?)e.Attribute("Text"))?.Contains("EditInstallLocation")==true);
        // Preserve the actual ancestor gates and the complete history grid. Remove
        // unrelated form fields/resources so this is an isolated, non-visible WPF test.
        foreach(var e in layout.Descendants().Reverse().ToArray())
            if(e!=history && !e.Ancestors().Contains(history) && e!=editor && !e.Descendants().Contains(history) && !e.Descendants().Contains(editor)) e.Remove();
        foreach(var e in layout.DescendantsAndSelf())
        {
            e.Attribute("Style")?.Remove();
            e.Attribute("Grid.Row")?.Remove(); e.Attribute("Grid.Column")?.Remove(); e.Attribute("Grid.ColumnSpan")?.Remove();
            e.Attribute("PreviewMouseRightButtonDown")?.Remove();
        }
        return (FrameworkElement)XamlReader.Parse(layout.ToString());
    }

    private static IEnumerable<T> Find<T>(DependencyObject root) where T:DependencyObject
    {
        for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
        {
            var child=VisualTreeHelper.GetChild(root,i);
            if(child is T value) yield return value;
            foreach(var descendant in Find<T>(child)) yield return descendant;
        }
    }

    private static Task OnSta(Func<Task> action)
    {
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var dispatcher=Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async ()=>
            {
                try { await action(); completion.SetResult(); }
                catch(Exception error) { completion.SetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground=true };
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
