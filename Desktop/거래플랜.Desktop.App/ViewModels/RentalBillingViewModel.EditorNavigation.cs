using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Services;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingViewModel
{
    private bool _editorResetInProgress;
    public bool IsEditorNavigationAvailable => !_editorResetInProgress;
    public Func<string, bool>? ConfirmEditorDiscard { get; set; }

    private bool HasPendingNavigationEdits()
        => !LegacyDraftRecovery.IsPristineEditor && HasMeaningfulDraftState() &&
           (string.IsNullOrWhiteSpace(_selectedRowBaselineSignature) || HasUnsavedEditorChangesAgainstBaseline());

    // Only user selection goes through this gate. Reload and save already preserve
    // their captured editor through the existing selection pipeline.
    public bool TrySelectEditorRow(RentalBillingViewRow? row)
    {
        if (ReferenceEquals(row, SelectedRow))
            return true;
        if (row is null || !Rows.Contains(row) || IsBusy || _editorResetInProgress || IsAutoSaveSuppressed)
            return false;
        if (HasPendingNavigationEdits())
        {
            StatusMessage = "저장하지 않은 편집 내용을 보존했습니다. 저장하거나 '편집 취소'를 확인한 뒤 목록에서 선택하세요.";
            return false;
        }

        SelectedRow = row;
        return true;
    }

    [RelayCommand]
    private async Task CancelEditingAsync()
    {
        if (await ResetEditorForNavigationAsync())
        {
            await ReloadAsync();
            StatusMessage = "미저장 편집을 취소했습니다. 목록에서 대상을 다시 선택하면 최신 저장값을 불러옵니다.";
        }
    }

    private async Task<bool> ResetEditorForNavigationAsync()
    {
        if (_isDisposed || IsBusy || _editorResetInProgress || IsAutoSaveSuppressed)
            return false;

        var signature = BuildCurrentEditorSignature();
        var epoch = _session.SyncScopeEpoch;
        if (HasPendingNavigationEdits() && ConfirmEditorDiscard?.Invoke(
                "현재 저장하지 않은 입력과 자동저장 임시본을 취소할까요?\n저장된 청구 설정·전표·입금은 유지됩니다.\n취소 후 목록에서 대상을 다시 선택하면 최신 저장값을 불러옵니다.") != true)
        {
            StatusMessage = "편집 내용과 임시본을 유지했습니다.";
            return false;
        }

        _editorResetInProgress = true;
        OnPropertyChanged(nameof(IsEditorNavigationAvailable));
        try
        {
            CancelPendingFilterReload();
            await CancelAndDrainPendingSelectionLoadsAsync();
            var ct = _lifetimeCts.Token;
            await _autoSaveGate.WaitAsync(ct);
            try
            {
                _autoSaveCts?.Cancel();
                BeginAutoSaveSuppression();
                try
                {
                    await _filterReloadGate.WaitAsync(ct);
                    try
                    {
                        var reset = false;
                        await _selectionPipelineCoordinator.RunExclusiveAsync(async token =>
                        {
                            await CancelAndDrainPhaseLoadsAsync();
                            if (_session.SyncScopeEpoch != epoch || BuildCurrentEditorSignature() != signature)
                            {
                                StatusMessage = "확인 중 계정 또는 편집 내용이 변경되어 취소하지 않았습니다. 현재 입력을 다시 확인하세요.";
                                return;
                            }

                            await ClearAutoSaveDraftCoreAsync(token);
                            if (_session.SyncScopeEpoch != epoch)
                                return;
                            if (BuildCurrentEditorSignature() != signature)
                            {
                                await _rental.SaveBillingEditorDraftAsync(BuildBillingEditorDraft(), _session, token);
                                StatusMessage = "취소 처리 중 새로 입력된 내용을 보존했습니다. 다시 확인하세요.";
                                return;
                            }
                            ResetNewProfileEditor();
                            reset = true;
                        }, ct);
                        return reset;
                    }
                    finally { _filterReloadGate.Release(); }
                }
                finally { EndAutoSaveSuppression(); }
            }
            finally { _autoSaveGate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            StatusMessage = $"편집 취소를 완료하지 못했습니다. 현재 입력을 유지합니다. {ex.Message}";
            return false;
        }
        finally
        {
            _editorResetInProgress = false;
            OnPropertyChanged(nameof(IsEditorNavigationAvailable));
            if (!_isDisposed && HasPendingNavigationEdits())
                ScheduleAutoSave();
        }
    }
}
