using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalLegacyDraftRecoveryState : ObservableObject
{
    private readonly RentalStateService _rental;
    private readonly SessionState _session;
    private readonly RentalDraftKind _kind;
    private readonly Func<Task> _flush;
    private readonly Func<string> _captureEditor;
    private readonly Func<Task> _restore;
    private readonly Func<bool> _isBusy;
    private readonly Action<string> _setStatus;
    private string? _pristineEditor;
    public bool IsPristineEditor => _pristineEditor is not null && _captureEditor() == _pristineEditor;
    public void MarkPristineEditor() => _pristineEditor = _captureEditor();

    [ObservableProperty] private bool _isAvailable;
    public Func<RentalLegacyDraftPreview, Task<bool>>? ConfirmRecoveryAsync { get; set; }

    public RentalLegacyDraftRecoveryState(RentalStateService rental, SessionState session, RentalDraftKind kind,
        Func<Task> flush, Func<string> captureEditor, Func<Task> restore, Func<bool> isBusy, Action<string> setStatus)
    {
        _rental = rental; _session = session; _kind = kind; _flush = flush;
        _captureEditor = captureEditor; _restore = restore; _isBusy = isBusy; _setStatus = setStatus;
    }

    public async Task RefreshAsync()
    {
        try { IsAvailable = await _rental.GetLegacyDraftPreviewAsync(_kind, _session) is not null; }
        catch (InvalidOperationException ex) { IsAvailable = true; _setStatus(ex.Message); }
    }

    [RelayCommand]
    private async Task RecoverAsync()
    {
        if (_isBusy() || ConfirmRecoveryAsync is null) return;
        try
        {
            if (!IsPristineEditor) await _flush();
            var preview = await _rental.GetLegacyDraftPreviewAsync(_kind, _session);
            if (preview is null) { IsAvailable = false; _setStatus("복원할 이전 임시본이 없습니다."); return; }
            if (preview.TargetHasDraft)
            {
                _setStatus("현재 작성 중인 임시본이 있습니다. 먼저 저장하거나 내용을 정리한 뒤 이전 임시본을 확인해 주세요.");
                return;
            }
            var editor = _captureEditor();
            if (!await ConfirmRecoveryAsync(preview)) { _setStatus("이전 임시본 복원을 취소했습니다. 원문은 보관되어 있습니다."); return; }
            if (_isBusy() || _captureEditor() != editor)
                throw new InvalidOperationException("확인 중 편집 내용이 변경되었습니다. 현재 내용을 정리한 뒤 다시 확인해 주세요.");
            await _rental.RecoverLegacyDraftAsync(preview, _session);
            IsAvailable = false;
            await _restore();
            _setStatus("확인한 업체의 편집 화면으로 임시본을 복원했습니다. 내용을 확인한 뒤 저장하세요.");
        }
        catch (Exception ex) { _setStatus($"이전 임시본 복원을 완료하지 못했습니다. {ex.Message}"); }
    }
}
