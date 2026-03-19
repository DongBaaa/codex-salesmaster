using Microsoft.Extensions.DependencyInjection;

namespace GeoraePlan.Mobile.App;

public static class ServiceHelper
{
    public static IServiceProvider Services { get; set; } = default!;

    public static T GetRequiredService<T>() where T : notnull
        => Services.GetRequiredService<T>();
}
