using System.Windows;
using System.Windows.Threading;
using 거래플랜.Desktop.App.Infrastructure;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.Services;

public sealed record EditSessionSubject(
    string EntityType,
    string EntityId,
    string DisplayName);

public sealed class EntityEditSessionMonitor : IDisposable
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private readonly Window _owner;
    private readonly ErpApiClient _api;
    private readonly SessionState _session;
    private readonly string _screenName;
    private readonly Func<EditSessionSubject?> _subjectAccessor;
    private readonly DispatcherTimer _timer;
    private readonly Guid _editSessionId = Guid.NewGuid();
    private readonly string _baseTitle;

    private bool _started;
    private bool _disposed;
    private bool _heartbeatInProgress;
    private string _lastWarningSignature = string.Empty;

    private EntityEditSessionMonitor(
        Window owner,
        ErpApiClient api,
        SessionState session,
        string screenName,
        Func<EditSessionSubject?> subjectAccessor)
    {
        _owner = owner;
        _api = api;
        _session = session;
        _screenName = screenName;
        _subjectAccessor = subjectAccessor;
        _baseTitle = owner.Title;
        _timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher)
        {
            Interval = HeartbeatInterval
        };
        _timer.Tick += (_, _) => UiTaskHelper.Forget(
            SendHeartbeatAsync(CancellationToken.None),
            "EDIT-SESSION",
            $"{screenName} 편집 세션 하트비트",
            ex => AppLogger.Warn("EDIT-SESSION", $"{screenName} 편집 세션 하트비트 실패: {ex.Message}"));
    }

    public static EntityEditSessionMonitor? TryCreate(
        Window owner,
        string screenName,
        Func<EditSessionSubject?> subjectAccessor)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(screenName);
        ArgumentNullException.ThrowIfNull(subjectAccessor);

        var mainWindow = Application.Current?.Windows
            .OfType<MainWindow>()
            .FirstOrDefault();

        if (mainWindow is null || mainWindow.SessionState is null)
            return null;

        return new EntityEditSessionMonitor(owner, mainWindow.ApiClient, mainWindow.SessionState, screenName, subjectAccessor);
    }

    public void Start()
    {
        if (_disposed || _started || !_session.IsLoggedIn || _session.IsOfflineMode)
            return;

        _started = true;
        _timer.Start();
        UiTaskHelper.Forget(
            SendHeartbeatAsync(CancellationToken.None),
            "EDIT-SESSION",
            $"{_screenName} 편집 세션 시작",
            ex => AppLogger.Warn("EDIT-SESSION", $"{_screenName} 편집 세션 시작 실패: {ex.Message}"));
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _timer.Stop();
        RestoreWindowTitle();

        if (!_session.IsOfflineMode && _session.IsLoggedIn)
        {
            UiTaskHelper.Forget(
                ReleaseAsync(CancellationToken.None),
                "EDIT-SESSION",
                $"{_screenName} 편집 세션 종료",
                ex => AppLogger.Warn("EDIT-SESSION", $"{_screenName} 편집 세션 종료 실패: {ex.Message}"));
        }
    }

    private async Task SendHeartbeatAsync(CancellationToken ct)
    {
        if (_disposed || _heartbeatInProgress || !_session.IsLoggedIn || _session.IsOfflineMode)
            return;

        var subject = _subjectAccessor();
        if (subject is null ||
            string.IsNullOrWhiteSpace(subject.EntityType) ||
            string.IsNullOrWhiteSpace(subject.EntityId))
        {
            RestoreWindowTitle();
            _lastWarningSignature = string.Empty;
            return;
        }

        _heartbeatInProgress = true;
        try
        {
            var response = await _api.HeartbeatEditSessionAsync(new EditSessionHeartbeatRequest
            {
                EditSessionId = _editSessionId,
                AppSessionId = _session.SessionId,
                ScreenName = _screenName,
                EntityType = subject.EntityType,
                EntityId = subject.EntityId,
                EntityDisplayName = subject.DisplayName,
                MachineName = Environment.MachineName
            }, ct);

            ApplyParticipants(subject, response?.OtherEditors ?? []);
        }
        finally
        {
            _heartbeatInProgress = false;
        }
    }

    private async Task ReleaseAsync(CancellationToken ct)
    {
        await _api.ReleaseEditSessionAsync(new EditSessionReleaseRequest
        {
            EditSessionId = _editSessionId
        }, ct);
    }

    private void ApplyParticipants(EditSessionSubject subject, IReadOnlyList<EditSessionParticipantDto> others)
    {
        if (_disposed)
            return;

        if (others.Count == 0)
        {
            RestoreWindowTitle();
            _lastWarningSignature = string.Empty;
            return;
        }

        var first = others[0];
        var suffix = others.Count == 1
            ? $" [다른 PC 편집중: {first.Username} / {first.OfficeCode}]"
            : $" [다른 PC 편집중: {first.Username} 외 {others.Count - 1}명]";
        _owner.Title = _baseTitle + suffix;

        var warningSignature = string.Join(
            "|",
            others.Select(current => $"{current.Username}@{current.MachineName}@{current.OfficeCode}"));
        if (string.Equals(_lastWarningSignature, warningSignature, StringComparison.Ordinal))
            return;

        _lastWarningSignature = warningSignature;
        var lines = others
            .Select(current => $"- {current.Username} / {current.OfficeCode} / {current.MachineName}")
            .ToList();
        var subjectDisplay = string.IsNullOrWhiteSpace(subject.DisplayName)
            ? subject.EntityType
            : subject.DisplayName;

        MessageBox.Show(
            _owner,
            $"[{subjectDisplay}] 항목을 다른 PC에서도 편집 중입니다.{Environment.NewLine}{Environment.NewLine}{string.Join(Environment.NewLine, lines)}{Environment.NewLine}{Environment.NewLine}저장 전에 최신 내용을 다시 확인하세요.",
            "동시 편집 알림",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void RestoreWindowTitle()
    {
        if (!_owner.Dispatcher.CheckAccess())
        {
            _owner.Dispatcher.Invoke(RestoreWindowTitle);
            return;
        }

        if (!string.Equals(_owner.Title, _baseTitle, StringComparison.Ordinal))
            _owner.Title = _baseTitle;
    }
}
