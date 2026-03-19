using GeoraePlan.Mobile.App.Pages;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Extensions.Logging;

namespace GeoraePlan.Mobile.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<SessionStore>();
        builder.Services.AddSingleton<JsonSyncStateStore>();
        builder.Services.AddSingleton<GeoraePlanApiClient>();
        builder.Services.AddSingleton<SyncCoordinator>();

        builder.Services.AddSingleton<LoginViewModel>();
        builder.Services.AddSingleton<HomeViewModel>();
        builder.Services.AddSingleton<CustomersViewModel>();
        builder.Services.AddSingleton<ItemsViewModel>();
        builder.Services.AddSingleton<InvoicesViewModel>();
        builder.Services.AddSingleton<SyncViewModel>();
        builder.Services.AddSingleton<SettingsViewModel>();
        builder.Services.AddTransient<InvoiceDraftViewModel>();
        builder.Services.AddTransient<PaymentDraftViewModel>();

        builder.Services.AddTransient<LoginPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<CustomersPage>();
        builder.Services.AddTransient<ItemsPage>();
        builder.Services.AddTransient<InvoicesPage>();
        builder.Services.AddTransient<SyncPage>();
        builder.Services.AddTransient<SettingsPage>();
        builder.Services.AddTransient<InvoiceDraftPage>();
        builder.Services.AddTransient<PaymentDraftPage>();

        var app = builder.Build();
        ServiceHelper.Services = app.Services;
        return app;
    }
}
