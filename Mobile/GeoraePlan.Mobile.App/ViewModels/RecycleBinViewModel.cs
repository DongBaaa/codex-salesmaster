using System.Collections.ObjectModel;
using GeoraePlan.Mobile.App.Models;
using GeoraePlan.Mobile.App.Services;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.ViewModels;

public sealed class RecycleBinViewModel : ObservableObject
{
    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;
    private readonly MobileOwnerOperationGate _ownerOperations;

    private string _searchText = string.Empty;
    private string _selectedKind = string.Empty;
    private string _statusMessage = "운영 서버 휴지통을 조회하세요.";
    private bool _isBusy;

    public RecycleBinViewModel(GeoraePlanApiClient api, SessionStore sessionStore)
    {
        _api = api;
        _sessionStore = sessionStore;
        _ownerOperations =
            new MobileOwnerOperationGate(sessionStore);
        RefreshCommand = new AsyncCommand(RefreshAsync);

        KindOptions.Add(new RecycleBinFilterOption(string.Empty, "전체"));
        KindOptions.Add(new RecycleBinFilterOption("customer", "거래처"));
        KindOptions.Add(new RecycleBinFilterOption("contract", "계약서"));
        KindOptions.Add(new RecycleBinFilterOption("item", "품목"));
        KindOptions.Add(new RecycleBinFilterOption("company-profile", "회사설정"));
        KindOptions.Add(new RecycleBinFilterOption("customer-category", "고객분류"));
        KindOptions.Add(new RecycleBinFilterOption("price-grade-option", "가격등급"));
        KindOptions.Add(new RecycleBinFilterOption("trade-type-option", "거래구분"));
        KindOptions.Add(new RecycleBinFilterOption("item-category-option", "품목분류"));
        KindOptions.Add(new RecycleBinFilterOption("invoice", "전표"));
        KindOptions.Add(new RecycleBinFilterOption("payment", "수금/지급"));
        KindOptions.Add(new RecycleBinFilterOption("transaction", "거래내역"));
        KindOptions.Add(new RecycleBinFilterOption("inventory-transfer", "재고이동"));
        KindOptions.Add(new RecycleBinFilterOption("rental-management-company", "렌탈 관리업체"));
        KindOptions.Add(new RecycleBinFilterOption("rental-billing-profile", "렌탈 청구프로필"));
        KindOptions.Add(new RecycleBinFilterOption("rental-asset", "렌탈 자산"));
        KindOptions.Add(new RecycleBinFilterOption("rental-billing-log", "렌탈 청구로그"));
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
        var operation = _ownerOperations.TryBegin(
            ClearStaleOwnerView,
            deferRefreshWhenBusy: true);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        if (!CanManageRecycleBinData)
        {
            ReplaceEntries([]);
            StatusMessage = "휴지통 조회/복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
            return;
        }

        var owner = operation.Owner;
        var runDeferredRefresh = false;
        try
        {
            await RefreshCoreAsync(owner);
        }
        catch (StaleMobileSessionOwnerException)
        {
            if (_ownerOperations.CanCommit(operation))
                ClearStaleOwnerView();
        }
        catch (MobileClientUpgradeRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_ownerOperations.CanCommit(operation))
                StatusMessage = $"휴지통 조회 실패: {ex.Message}";
        }
        finally
        {
            runDeferredRefresh = _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
            if (runDeferredRefresh)
                await RefreshAsync();
        }
    }

    public async Task RestoreAsync(RecycleBinEntryDto entry)
    {
        if (entry is null)
            return;

        var operation = _ownerOperations.TryBegin(
            ClearStaleOwnerView,
            deferRefreshWhenBusy: false);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
            return;
        }

        var currentEntry = Entries.FirstOrDefault(
            candidate =>
                candidate.EntityId == entry.EntityId &&
                candidate.Revision == entry.Revision &&
                string.Equals(
                    candidate.Kind,
                    entry.Kind,
                    StringComparison.OrdinalIgnoreCase));
        if (currentEntry is null)
        {
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
            return;
        }

        var owner = operation.Owner;
        var target = new RecycleBinMutationTargetDto
        {
            EntityId = currentEntry.EntityId,
            Kind = currentEntry.Kind,
            ExpectedRevision = currentEntry.Revision
        };
        try
        {
            _sessionStore.ThrowIfOwnerChanged(owner);
            var result = await _api.RestoreRecycleBinAsync(
                [target],
                owner);

            await RefreshCoreAsync(owner);
            _sessionStore.ThrowIfOwnerChanged(owner);
            StatusMessage = BuildMutationMessage("복원", result);
        }
        catch (StaleMobileSessionOwnerException)
        {
            if (_ownerOperations.CanCommit(operation))
                ClearStaleOwnerView();
        }
        catch (MobileClientUpgradeRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_ownerOperations.CanCommit(operation))
                StatusMessage = $"휴지통 복원 실패: {ex.Message}";
        }
        finally
        {
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
        }
    }

    public async Task PurgeAsync(RecycleBinEntryDto entry)
    {
        if (entry is null)
            return;

        var operation = _ownerOperations.TryBegin(
            ClearStaleOwnerView,
            deferRefreshWhenBusy: false);
        IsBusy = _ownerOperations.IsBusy;
        if (operation is null)
            return;

        if (!CanManageRecycleBinData)
        {
            StatusMessage = "휴지통 영구삭제 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
            return;
        }

        var currentEntry = Entries.FirstOrDefault(
            candidate =>
                candidate.EntityId == entry.EntityId &&
                candidate.Revision == entry.Revision &&
                string.Equals(
                    candidate.Kind,
                    entry.Kind,
                    StringComparison.OrdinalIgnoreCase));
        if (currentEntry is null)
        {
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
            return;
        }

        var owner = operation.Owner;
        var target = new RecycleBinMutationTargetDto
        {
            EntityId = currentEntry.EntityId,
            Kind = currentEntry.Kind,
            ExpectedRevision = currentEntry.Revision
        };
        try
        {
            _sessionStore.ThrowIfOwnerChanged(owner);
            var result = await _api.PurgeRecycleBinAsync(
                [target],
                owner);

            await RefreshCoreAsync(owner);
            _sessionStore.ThrowIfOwnerChanged(owner);
            StatusMessage = BuildMutationMessage("영구삭제", result);
        }
        catch (StaleMobileSessionOwnerException)
        {
            if (_ownerOperations.CanCommit(operation))
                ClearStaleOwnerView();
        }
        catch (MobileClientUpgradeRequiredException)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (_ownerOperations.CanCommit(operation))
                StatusMessage = $"휴지통 영구삭제 실패: {ex.Message}";
        }
        finally
        {
            _ownerOperations.Complete(
                operation,
                ClearStaleOwnerView);
            IsBusy = _ownerOperations.IsBusy;
        }
    }

    private void ReplaceEntries(IReadOnlyList<RecycleBinEntryDto> entries)
    {
        Entries.Clear();
        foreach (var entry in entries)
            Entries.Add(entry);
    }

    private async Task RefreshCoreAsync(
        MobileSessionOwner owner)
    {
        _sessionStore.ThrowIfOwnerChanged(owner);
        if (!CanManageRecycleBinData)
        {
            ReplaceEntries([]);
            StatusMessage = "휴지통 조회/복원 권한이 없습니다. 관리자에게 Data.BackupRestore 권한을 요청하세요.";
            return;
        }

        StatusMessage = "휴지통을 조회하고 있습니다.";
        var selectedKind = SelectedKind;
        var searchText = SearchText;
        var result = await _api.GetRecycleBinAsync(
            selectedKind,
            searchText,
            owner);
        _sessionStore.ThrowIfOwnerChanged(owner);
        if (!CanManageRecycleBinData)
        {
            ReplaceEntries([]);
            StatusMessage =
                "휴지통 권한이 변경되어 표시 중인 결과를 비웠습니다.";
            return;
        }
        ReplaceEntries(result);
        StatusMessage = result.Count == 0
            ? "휴지통이 비어 있습니다."
            : $"휴지통 {result.Count:N0}건";
    }

    private void ClearStaleOwnerView()
    {
        ReplaceEntries([]);
        SearchText = string.Empty;
        SelectedKind = string.Empty;
        StatusMessage =
            "로그인 범위가 변경되어 휴지통 결과를 비우고 작업을 중단했습니다.";
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
