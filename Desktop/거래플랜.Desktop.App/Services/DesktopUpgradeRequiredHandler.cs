using System.Net;
using System.Net.Http;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Intercepts HTTP 426 inside the handler pipeline, before HttpClient can
/// buffer a response body for the convenience *Async methods.
/// </summary>
public sealed class DesktopUpgradeRequiredHandler : DelegatingHandler
{
    private readonly IDesktopUpgradeRequiredObserver? _observer;

    public DesktopUpgradeRequiredHandler(
        IDesktopUpgradeRequiredObserver? observer = null)
    {
        _observer = observer;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(
                request,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.UpgradeRequired)
            return response;

        try
        {
            var exception =
                await DesktopUpgradeRequiredResponseParser
                .CreateExceptionAsync(
                    request.RequestUri?.PathAndQuery ?? string.Empty,
                    response.Content,
                    cancellationToken)
                .ConfigureAwait(false);
            if (_observer is not null)
            {
                try
                {
                    await _observer.ObserveAsync(
                            exception,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception observerException)
                {
                    AppLogger.Error(
                        "UPDATE",
                        "Desktop 426 observer failed; preserving the original typed exception.",
                        observerException);
                }
            }

            throw exception;
        }
        finally
        {
            response.Dispose();
        }
    }
}
