using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;
using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class EditSessionSubjectRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SubjectChangedDuringHeartbeat_RegistersLatestWithoutWaitingForTimer(bool firstForbidden)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyAsync(firstForbidden); completion.SetResult(); }
                catch(Exception ex) { completion.SetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(5000));
    }

    private static async Task VerifyAsync(bool firstForbidden)
    {
        using var handler = new DelayedHandler(firstForbidden);
        using var http = new HttpClient(handler) { BaseAddress=new Uri("http://127.0.0.1/") };
        var session = new SessionState();
        session.SetSession("synthetic-test-token", new UserSessionDto { Username="synthetic",Role="User",OfficeCode="USENET",TenantCode="USENET_GROUP",ScopeType="OfficeOnly" });
        var api = new ErpApiClient(http,session);
        EditSessionSubject? subject = new("Invoice",Guid.NewGuid().ToString("D"),"synthetic first");
        var initialId = subject.EntityId;
        var constructor = typeof(EntityEditSessionMonitor).GetConstructors(BindingFlags.Instance|BindingFlags.NonPublic).Single();
        var refresh = typeof(EntityEditSessionMonitor).GetMethod("RequestSubjectRefresh");
        Assert.NotNull(refresh);
        using var monitor = (EntityEditSessionMonitor)constructor.Invoke([new Window {Title="refresh test"},api,session,"test",(Func<EditSessionSubject?>)(()=>subject)]);
        monitor.Start();
        await handler.FirstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        subject = new("Invoice",Guid.NewGuid().ToString("D"),"synthetic latest");
        refresh.Invoke(monitor,null);
        refresh.Invoke(monitor,null);
        handler.CompleteFirst.SetResult();
        await handler.SecondFinished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        for(var attempt=0; attempt<100 && (bool)typeof(EntityEditSessionMonitor).GetField("_heartbeatInProgress",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(monitor)!;attempt++)
            await Task.Delay(10);
        Assert.Equal(new[]{initialId,subject.EntityId},handler.Ids);
        Assert.Equal(1,handler.MaxConcurrent);
        subject=null;
        refresh.Invoke(monitor,null);
        await handler.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2,handler.Ids.Count);
    }

    private sealed class DelayedHandler(bool firstForbidden) : HttpMessageHandler
    {
        public List<string> Ids {get;}=[];
        public TaskCompletionSource FirstStarted {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CompleteFirst {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource SecondFinished {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Released {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int MaxConcurrent {get;private set;}
        private int concurrent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            concurrent++; MaxConcurrent=Math.Max(MaxConcurrent,concurrent);
            try
            {
                if(request.RequestUri!.AbsolutePath.EndsWith("/release"))
                {
                    Released.TrySetResult(); return new(HttpStatusCode.OK);
                }
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Ids.Add(body.RootElement.GetProperty("entityId").GetString()!);
                if(Ids.Count==1)
                {
                    FirstStarted.SetResult(); await CompleteFirst.Task.WaitAsync(ct);
                    if(firstForbidden) return new(HttpStatusCode.Forbidden);
                }
                if(Ids.Count==2) SecondFinished.TrySetResult();
                return new(HttpStatusCode.OK) {Content=new StringContent("{\"otherEditors\":[]}",Encoding.UTF8,"application/json")};
            }
            finally { concurrent--; }
        }
    }
}
