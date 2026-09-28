using 거래플랜.Server.Api.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Server.Api.Middleware;

// A schema boundary, independent of the configurable rollout gate's AuditOnly mode.
public sealed class InvoiceAmountCompatibilityMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, OfficeScopeService scope)
    {
        var catalogResponse = context.Request.Path.StartsWithSegments("/items", StringComparison.OrdinalIgnoreCase)
            || context.Request.Path.StartsWithSegments("/sync", StringComparison.OrdinalIgnoreCase);
        var minimumProtocol = context.Request.Path.StartsWithSegments("/sync", StringComparison.OrdinalIgnoreCase)
            && (!scope.CanViewSalesAmounts() || !scope.CanViewPurchaseAmounts())
            ? ClientCompatibilityHeaders.NullableRentalProfileAmountsProtocolVersion
            : catalogResponse
            ? ClientCompatibilityHeaders.NullableItemAmountsProtocolVersion
            : ClientCompatibilityHeaders.NullableInvoiceAmountsProtocolVersion;
        if (context.User.Identity?.IsAuthenticated != true ||
            (scope.CanViewSalesAmounts() && scope.CanViewPurchaseAmounts()) ||
            !(catalogResponse || context.Request.Path.StartsWithSegments("/invoices", StringComparison.OrdinalIgnoreCase) ||
              context.Request.Path.StartsWithSegments("/payments", StringComparison.OrdinalIgnoreCase) ||
              (context.Request.Path.StartsWithSegments("/customers", StringComparison.OrdinalIgnoreCase) &&
               (context.Request.Path.Value?.TrimEnd('/').EndsWith("/detail", StringComparison.OrdinalIgnoreCase) ?? false))))
        {
            await next(context);
            return;
        }

        context.Response.Headers.CacheControl = "no-store";
        var parsed = ClientCompatibilityGateMiddleware.ClientCompatibilityIdentityParser.Parse(context.Request.Headers);
        var client = parsed.Identity;
        if (parsed.Success && client!.ProtocolVersion >= minimumProtocol)
        {
            await next(context);
            return;
        }
        context.Response.StatusCode = StatusCodes.Status426UpgradeRequired;
        context.Response.Headers.Upgrade = "georaeplan-client";
        await context.Response.WriteAsJsonAsync(new ClientUpgradeRequiredResponse
        {
            Message = "비공개 금액을 안전하게 처리하려면 거래플랜 앱을 업데이트해 주세요.",
            Upgrade = "georaeplan-client",
            Client = new ClientCompatibilityIdentityDto
            {
                AppId = client?.AppId ?? string.Empty, Platform = client?.Platform ?? string.Empty,
                Version = client?.Version ?? string.Empty, Build = client?.Build ?? 0,
                ProtocolVersion = client?.ProtocolVersion ?? 0
            },
            Required = new ClientCompatibilityPolicyDto
            {
                PolicyVersion = 1, RequiresUserAction = true,
                MinimumProtocolVersion = minimumProtocol
            }
        }, cancellationToken: context.RequestAborted);
    }
}
