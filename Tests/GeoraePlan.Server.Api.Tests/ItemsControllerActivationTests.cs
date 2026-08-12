using Microsoft.Extensions.DependencyInjection;
using Xunit;
using 거래플랜.Server.Api.Controllers;

namespace GeoraePlan.Server.Api.Tests;

public sealed class ItemsControllerActivationTests
{
    [Fact]
    public void RuntimeFactory_SelectsTheMarkedDependencyInjectionConstructor()
    {
        var factory = ActivatorUtilities.CreateFactory(
            typeof(ItemsController),
            Type.EmptyTypes);

        Assert.NotNull(factory);
    }
}
