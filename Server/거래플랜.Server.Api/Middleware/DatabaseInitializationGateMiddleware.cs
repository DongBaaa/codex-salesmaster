using 거래플랜.Server.Api.Services;

namespace 거래플랜.Server.Api.Middleware;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, Inherited = true)]
public sealed class AllowDuringDatabaseInitializationAttribute : Attribute
{
}

public sealed class DatabaseInitializationGateMiddleware
{
    private const int RetryAfterSeconds = 5;
    private readonly RequestDelegate _next;

    public DatabaseInitializationGateMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(
        HttpContext context,
        DatabaseInitializationState databaseInitializationState)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AllowDuringDatabaseInitializationAttribute>() is not null)
        {
            await _next(context);
            return;
        }

        var snapshot = databaseInitializationState.CreateSnapshot();
        if (snapshot.Completed && !snapshot.Failed)
        {
            await _next(context);
            return;
        }

        context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(
            System.Globalization.CultureInfo.InvariantCulture);
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(
            new
            {
                error = "database_initialization_unavailable",
                reason = snapshot.Failed
                    ? "database_initialization_failed"
                    : snapshot.Started
                        ? "database_initialization_running"
                        : "database_initialization_not_started"
            },
            cancellationToken: context.RequestAborted);
    }
}
