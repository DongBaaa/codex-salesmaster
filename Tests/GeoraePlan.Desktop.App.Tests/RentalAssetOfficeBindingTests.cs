using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Desktop.App.ViewModels;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class RentalAssetOfficeBindingTests
{
    [Theory]
    [InlineData("USENET", "ITWORLD")]
    [InlineData("ITWORLD", "USENET")]
    [InlineData("USENET", "YEONSU")]
    public Task SharedAssetSelection_ShowsResponsibleOfficeWithoutGrantingWriteAccess(string office, string assetOffice)
        => OnSta(async () =>
        {
            await using var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            await using var db = new LocalDbContext(new DbContextOptionsBuilder<LocalDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var session = new SessionState();
            session.SetOfflineSession(new UserSessionDto { Username="office-binding-test", Role="User", OfficeCode=office,
                TenantCode=office=="ITWORLD" ? "ITWORLD" : "USENET_GROUP", ScopeType="OfficeOnly" });
            var local = new LocalStateService(db, new OfficeAccessService(), new SyncRequestDispatcher(), session);
            var rental = new RentalStateService(db, local);
            var vm = new RentalAssetViewModel(rental, local, new RentalDocumentService(), null!, session);
            vm.EditOfficeOptions.Add(new DisplayOption { Value=office, DisplayName=OfficeCodeCatalog.GetOfficeDisplayName(office) });
            vm.EditOfficeCode=office;
            var selector=ReadActualOfficeSelector(); selector.DataContext=vm;
            try
            {
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Assert.Equal(office, selector.SelectedValue);
                foreach (var target in new[]{assetOffice,office,assetOffice})
                {
                    // An empty id avoids asynchronous history I/O: this regression concerns
                    // the real editor selection and WPF two-way SelectedValue binding.
                    vm.SelectedRow=new RentalAssetViewRow { Source=new LocalRentalAsset {
                        Id=Guid.Empty, ResponsibleOfficeCode=target, ManagementCompanyCode=target,
                        OfficeCode=target, TenantCode=target=="ITWORLD" ? "ITWORLD" : "USENET_GROUP",
                        ManagementNumber="office-binding", AssetStatus="창고", IsDirty=false } };
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert.Equal(target, vm.EditOfficeCode);
                    Assert.Equal(target, selector.SelectedValue);
                    Assert.Equal(OfficeCodeCatalog.GetOfficeDisplayName(target), Assert.IsType<DisplayOption>(selector.SelectedItem).DisplayName);
                    Assert.False(selector.IsEnabled);
                    Assert.False(vm.CanEditOfficeSelection);
                    Assert.False(rental.CanEditAssetScope(target,session));
                    Assert.False(vm.SaveCommand.CanExecute(null));
                    Assert.False(vm.HasPendingChanges);
                    if (target != office)
                    {
                        var snapshot=typeof(RentalAssetViewModel).GetMethod("CaptureEditSnapshot", BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,null);
                        vm.EditOfficeCode=office;
                        vm.EditOfficeOptions.Remove(vm.EditOfficeOptions.Single(o=>o.Value==target));
                        typeof(RentalAssetViewModel).GetMethod("ApplySnapshot", BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(vm,[snapshot,true]);
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.Equal(target,selector.SelectedValue);
                        Assert.Equal(target,vm.EditOfficeCode);
                        Assert.False(selector.IsEnabled);
                        Assert.False(vm.HasPendingChanges);
                    }
                }
                Assert.Empty(await db.SyncOutboxEntries.ToListAsync());
                Assert.Empty(await db.RentalAssets.ToListAsync());
            }
            finally { BindingOperations.ClearAllBindings(selector); await vm.CancelPendingBackgroundWorkAsync(); }
        });

    private static ComboBox ReadActualOfficeSelector()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);
        while(root is not null && !Directory.Exists(Path.Combine(root.FullName,"Desktop"))) root=root.Parent;
        Assert.NotNull(root);
        var document=XDocument.Load(Path.Combine(root.FullName,"Desktop","거래플랜.Desktop.App","Views","RentalAssetWindow.xaml"));
        XNamespace ns="http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var element=document.Descendants(ns+"ComboBox").Single(e=>(string?)e.Attribute("SelectedValue")=="{Binding EditOfficeCode}");
        return (ComboBox)XamlReader.Parse(element.ToString());
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
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }
}
