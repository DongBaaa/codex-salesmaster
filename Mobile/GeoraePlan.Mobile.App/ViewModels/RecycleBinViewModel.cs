using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class RecycleBinViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;

    private string _searchText = string.Empty;
    private string _selectedKind = string.Empty;
    private string _statusMessage = "운영 서버 휴지통을 조회하세요.";
    private bool _isBusy;

    public RecycleBinViewModel(GeoraePlanApiClient api, SessionStore sessionStore)
    {
        _api = api;
        _sessionStore = sessionStore;
        RefreshCommand = new AsyncCommand(RefreshAsync);

        KindOptions.Add(new RecycleBinFilterOption(string.Empty, "전체"));
        KindOptions.Add(new RecycleBinFilterOption("customer", "거래처"));
        KindOptions.Add(new RecycleBinFilterOption("contract", "계약서"));
        KindOptions.Add(new RecycleBinFilterOption("item", "품목"));
        KindOptions.Add(new RecycleBinFilterOption("invoice", "전표"));
        KindOptions.Add(new RecycleBinFilterOption("payment", "수금/지급"));
    }

    public ObservableCollection<RecycleBinEntryDto> Entries { get; } = new();
    public ObservableCollection<RecycleBinFilterOption> KindOptions { get; } = new();
    public bool CanManageRecycleBinData => _sessionStore.GetSnapshot().CanManageRecycleBin;

    public string SearchText
    {
        get => _searchText;
        set => SetProperty(ref _searchText, value);
    }

    public string SelectedKind
    {
        get => _selectedKind;
        set => SetProperty(ref _selectedKind, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public AsyncCommand RefreshCommand { get; }

    public async Task RefreshAsync()
    {
        if (IsBusy)
            return;

        if (!CanManageRecycleBinData)
        {
            ReplaceEntries([]);
            StatusMessage = "휴지통 조회/복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            await RefreshCoreAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"휴지통 조회 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task RestoreAsync(RecycleBinEntryDto entry)
    {
        if (entry is null || IsBusy)
            return;

        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _api.RestoreRecycleBinAsync(
                [new RecycleBinMutationTargetDto { EntityId = entry.EntityId, Kind = entry.Kind, ExpectedRevision = entry.Revision }]);

            await RefreshCoreAsync();
            StatusMessage = BuildMutationMessage("복원", result);
        }
        catch (Exception ex)
        {
            StatusMessage = $"휴지통 복원 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task PurgeAsync(RecycleBinEntryDto entry)
    {
        if (entry is null || IsBusy)
            return;

        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 영구삭제 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        try
        {
            IsBusy = true;
            var result = await _api.PurgeRecycleBinAsync(
                [new RecycleBinMutationTargetDto { EntityId = entry.EntityId, Kind = entry.Kind, ExpectedRevision = entry.Revision }]);

            await RefreshCoreAsync();
            StatusMessage = BuildMutationMessage("영구삭제", result);
        }
        catch (Exception ex)
        {
            StatusMessage = $"휴지통 영구삭제 실패: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void ReplaceEntries(IReadOnlyList<RecycleBinEntryDto> entries)
    {
        Entries.Clear();
        foreach (var entry in entries)
            Entries.Add(entry);
    }

    private async Task RefreshCoreAsync()
    {
        if (!CanManageRecycleBinData)
        {
            ReplaceEntries([]);
            StatusMessage = "휴지통 조회/복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        StatusMessage = "휴지통을 조회하고 있습니다.";
        var result = await _api.GetRecycleBinAsync(SelectedKind, SearchText);
        ReplaceEntries(result);
        StatusMessage = result.Count == 0
            ? "휴지통이 비어 있습니다."
            : $"휴지통 {result.Count:N0}건";
    }

    private static string BuildMutationMessage(string action, RecycleBinMutationResultDto? result)
    {
        if (result is null)
            return $"휴지통 {action} 응답이 없습니다.";

        if (result.RequestedCount == 0)
            return $"휴지통 {action} 대상이 없습니다.";

        if (result.SucceededCount >= result.RequestedCount)
            return $"휴지통 항목을 {action}했습니다.";

        return result.Messages.FirstOrDefault()
               ?? $"휴지통 {action} 완료: 성공 {result.SucceededCount:N0}건 / 실패 {result.RequestedCount - result.SucceededCount:N0}건";
    }
}

public sealed record RecycleBinFilterOption(string Value, string DisplayName);
