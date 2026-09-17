using System.Runtime.CompilerServices;
using System.Windows.Documents;
using System.Windows.Threading;

namespace 거래플랜.Desktop.App.Services;

/// <summary>Authorization belongs to the rendered document, including copies held by an open print dialog.</summary>
public static class PrintDocumentAuthorization
{
    public const string DeniedMessage = "조회 권한 또는 로그인 범위가 변경되어 출력을 중단했습니다. 문서를 닫고 다시 조회하세요.";
    private static readonly ConditionalWeakTable<IDocumentPaginatorSource, Access> Documents = new();

    public static Func<bool> CaptureOwner(SessionState session)
    {
        var sessionId = session.SessionId;
        var epoch = session.SyncScopeEpoch;
        return () => session.IsLoggedIn && session.SessionId == sessionId && session.SyncScopeEpoch == epoch;
    }

    public static void Attach(IDocumentPaginatorSource document, Func<bool> canRead)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(canRead);
        var access = new Access(canRead);
        Documents.Add(document, access);
        if (!access.Check())
            throw new UnauthorizedAccessException(DeniedMessage);
    }

    public static bool Validate(IDocumentPaginatorSource document, out string? errorMessage)
    {
        var allowed = !Documents.TryGetValue(document, out var access) || access.Check();
        errorMessage = allowed ? null : DeniedMessage;
        return allowed;
    }

    public static IDisposable Monitor(IDocumentPaginatorSource document, Action onDenied)
        => new MonitorLease(document, onDenied);

    private sealed class Access(Func<bool> canRead)
    {
        private bool _revoked;
        public bool Check()
        {
            if (_revoked) return false;
            try
            {
                if (canRead()) return true;
            }
            catch (Exception ex)
            {
                // Failure to establish current scope must never authorize an old document.
                AppLogger.Warn("PRINT", $"출력 권한을 확인할 수 없어 문서를 무효화합니다: {ex.GetType().Name}");
            }
            _revoked = true;
            return false;
        }
    }

    private sealed class MonitorLease : IDisposable
    {
        private readonly DispatcherTimer _timer;
        private readonly IDocumentPaginatorSource _document;
        private readonly Action _onDenied;
        public MonitorLease(IDocumentPaginatorSource document, Action onDenied)
        {
            _document = document;
            _onDenied = onDenied;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += Tick;
            if (Documents.TryGetValue(document, out _)) _timer.Start();
            Tick(this, EventArgs.Empty);
        }
        private void Tick(object? sender, EventArgs args)
        {
            if (Validate(_document, out _)) return;
            Dispose();
            _onDenied();
        }
        public void Dispose()
        {
            _timer.Stop();
            _timer.Tick -= Tick;
        }
    }
}
