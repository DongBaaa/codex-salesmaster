using System.Collections.Specialized;
using System.ComponentModel;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalBillingViewModel
{
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private CancellationTokenSource? _autoSaveCts;
    private int _autoSaveSuppressionCount;

    private static readonly HashSet<string> TrackedAutoSaveProperties = new(StringComparer.Ordinal)
    {
        nameof(EditCustomerName),
        nameof(EditBusinessNumber),
        nameof(EditRealCustomerName),
        nameof(EditBillToCustomerName),
        nameof(EditInstallSiteName),
        nameof(EditItemName),
        nameof(EditBillingType),
        nameof(EditBillingAdvanceMode),
        nameof(EditOfficeCode),
        nameof(EditBillingMethod),
        nameof(EditPaymentMethod),
        nameof(EditBillingStatus),
        nameof(EditSettlementStatus),
        nameof(EditCompletionStatus),
        nameof(EditEmail),
        nameof(EditBillingDay),
        nameof(EditBillingDayMode),
        nameof(EditBillingCycleMonths),
        nameof(EditBillingAnchorMonth),
        nameof(EditDocumentIssueMode),
        nameof(EditDocumentLeadDays),
        nameof(EditMonthlyAmount),
        nameof(EditDepositAmount),
        nameof(EditSettledAmount),
        nameof(EditRequiresFollowUp),
        nameof(EditFollowUpNote),
        nameof(EditSubmissionDocuments),
        nameof(EditNotes),
        nameof(LinkAssetsLater),
        nameof(EditBillingAnchorDate),
        nameof(EditBillingStartDate),
        nameof(EditContractDate),
        nameof(EditContractStartDate),
        nameof(EditContractEndDate),
        nameof(EditLastBilledDate),
        nameof(EditLastSettledDate),
        nameof(EditIsActive)
    };

    private void InitializeAutoSave()
    {
        PropertyChanged += HandleAutoSavePropertyChanged;
        TemplateItems.CollectionChanged += HandleTemplateItemsChanged;
        foreach (var item in TemplateItems)
            WireTemplateItemAutoSave(item);
    }

    private bool IsAutoSaveSuppressed => _autoSaveSuppressionCount > 0;

    private void BeginAutoSaveSuppression() => _autoSaveSuppressionCount++;

    private void EndAutoSaveSuppression()
    {
        if (_autoSaveSuppressionCount > 0)
            _autoSaveSuppressionCount--;
    }

    public async Task<bool> RestoreAutoSaveDraftAsync()
    {
        var draft = await _rental.GetBillingEditorDraftAsync(_session);
        if (draft is null)
            return false;

        BeginAutoSaveSuppression();
        try
        {
            ApplyBillingEditorDraft(draft);
        }
        finally
        {
            EndAutoSaveSuppression();
        }

        if (!string.IsNullOrWhiteSpace(EditCustomerName) || TemplateItems.Any(item => item.IncludedAssetIds.Count > 0))
        {
            await LoadCandidateAssetsAsync(
                EditId == Guid.Empty ? null : EditId,
                EditCustomerName,
                EditBillToCustomerName,
                EditInstallSiteName,
                preserveSelection: true);
        }

        StatusMessage = "자동저장된 렌탈 청구 편집 내역을 불러왔습니다.";
        return true;
    }

    public async Task FlushAutoSaveAsync(CancellationToken ct = default)
    {
        _autoSaveCts?.Cancel();
        await PersistAutoSaveDraftAsync(ct);
    }

    public async Task FlushAutoSaveForCloseAsync(CancellationToken ct = default)
    {
        _candidateAssetsLoadCts?.Cancel();
        var candidateLoadTask = _candidateAssetsLoadTask;
        if (candidateLoadTask is not null)
        {
            try
            {
                await candidateLoadTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        await FlushAutoSaveAsync(ct);
    }

    public async Task ClearAutoSaveDraftAsync(CancellationToken ct = default)
    {
        _autoSaveCts?.Cancel();
        await _rental.ClearBillingEditorDraftAsync(_session, ct);
    }

    public void DiscardAutoSaveDraft()
    {
        _autoSaveCts?.Cancel();
        _ = SafeClearDraftAsync();
    }

    private async Task SafeClearDraftAsync()
    {
        try
        {
            await _rental.ClearBillingEditorDraftAsync(_session);
        }
        catch
        {
        }
    }

    private void HandleAutoSavePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsAutoSaveSuppressed || string.IsNullOrWhiteSpace(e.PropertyName))
            return;

        if (!TrackedAutoSaveProperties.Contains(e.PropertyName))
            return;

        ScheduleAutoSave();
    }

    private void HandleTemplateItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (var item in e.OldItems.OfType<RentalBillingTemplateEditorItem>())
                UnwireTemplateItemAutoSave(item);
        }

        if (e.NewItems is not null)
        {
            foreach (var item in e.NewItems.OfType<RentalBillingTemplateEditorItem>())
                WireTemplateItemAutoSave(item);
        }

        if (!IsAutoSaveSuppressed)
            ScheduleAutoSave();
    }

    private void WireTemplateItemAutoSave(RentalBillingTemplateEditorItem item)
    {
        item.PropertyChanged -= HandleTemplateItemPropertyChanged;
        item.PropertyChanged += HandleTemplateItemPropertyChanged;
        item.IncludedAssetIds.CollectionChanged -= HandleTemplateItemIncludedAssetIdsChanged;
        item.IncludedAssetIds.CollectionChanged += HandleTemplateItemIncludedAssetIdsChanged;
    }

    private void UnwireTemplateItemAutoSave(RentalBillingTemplateEditorItem item)
    {
        item.PropertyChanged -= HandleTemplateItemPropertyChanged;
        item.IncludedAssetIds.CollectionChanged -= HandleTemplateItemIncludedAssetIdsChanged;
    }

    private void HandleTemplateItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (IsAutoSaveSuppressed)
            return;

        ScheduleAutoSave();
    }

    private void HandleTemplateItemIncludedAssetIdsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (IsAutoSaveSuppressed)
            return;

        ScheduleAutoSave();
    }

    private void ScheduleAutoSave()
    {
        _autoSaveCts?.Cancel();
        _autoSaveCts?.Dispose();
        _autoSaveCts = new CancellationTokenSource();
        _ = RunAutoSaveAsync(_autoSaveCts.Token);
    }

    private async Task RunAutoSaveAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(700), token);
            await PersistAutoSaveDraftAsync(token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            StatusMessage = $"자동저장에 실패했습니다. {ex.Message}";
        }
    }

    private async Task PersistAutoSaveDraftAsync(CancellationToken ct)
    {
        if (IsAutoSaveSuppressed)
            return;

        await _autoSaveGate.WaitAsync(ct);
        try
        {
            if (HasMeaningfulDraftState())
            {
                await _rental.SaveBillingEditorDraftAsync(BuildBillingEditorDraft(), _session, ct);
            }
            else
            {
                await _rental.ClearBillingEditorDraftAsync(_session, ct);
            }
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    private RentalBillingEditorDraftModel BuildBillingEditorDraft()
        => new()
        {
            EditId = EditId,
            CustomerName = EditCustomerName,
            BusinessNumber = EditBusinessNumber,
            RealCustomerName = EditRealCustomerName,
            BillToCustomerName = EditBillToCustomerName,
            InstallSiteName = EditInstallSiteName,
            ItemName = EditItemName,
            BillingType = EditBillingType,
            BillingAdvanceMode = EditBillingAdvanceMode,
            OfficeCode = EditOfficeCode,
            BillingMethod = EditBillingMethod,
            PaymentMethod = EditPaymentMethod,
            BillingStatus = EditBillingStatus,
            SettlementStatus = EditSettlementStatus,
            CompletionStatus = EditCompletionStatus,
            Email = EditEmail,
            BillingDay = EditBillingDay,
            BillingDayMode = EditBillingDayMode,
            BillingCycleMonths = EditBillingCycleMonths,
            BillingAnchorMonth = EditBillingAnchorMonth,
            DocumentIssueMode = EditDocumentIssueMode,
            DocumentLeadDays = EditDocumentLeadDays,
            MonthlyAmount = EditMonthlyAmount,
            DepositAmount = EditDepositAmount,
            SettledAmount = EditSettledAmount,
            OutstandingAmount = EditOutstandingAmount,
            RequiresFollowUp = EditRequiresFollowUp,
            FollowUpNote = EditFollowUpNote,
            SubmissionDocuments = EditSubmissionDocuments,
            Notes = EditNotes,
            LinkAssetsLater = LinkAssetsLater,
            BillingAnchorDate = EditBillingAnchorDate,
            BillingStartDate = EditBillingStartDate,
            ContractDate = EditContractDate,
            ContractStartDate = EditContractStartDate,
            ContractEndDate = EditContractEndDate,
            LastBilledDate = EditLastBilledDate,
            LastSettledDate = EditLastSettledDate,
            IsActive = EditIsActive,
            SelectedTemplateItemId = SelectedTemplateItem?.ItemId,
            TemplateItems = ToTemplateModels()
        };

    private void ApplyBillingEditorDraft(RentalBillingEditorDraftModel draft)
    {
        SelectedRow = null;
        EditId = draft.EditId == Guid.Empty ? Guid.NewGuid() : draft.EditId;
        EditCustomerName = draft.CustomerName ?? string.Empty;
        EditBusinessNumber = draft.BusinessNumber ?? string.Empty;
        EditRealCustomerName = draft.RealCustomerName ?? string.Empty;
        EditBillToCustomerName = draft.BillToCustomerName ?? string.Empty;
        EditInstallSiteName = draft.InstallSiteName ?? string.Empty;
        EditItemName = draft.ItemName ?? string.Empty;
        EditBillingType = string.IsNullOrWhiteSpace(draft.BillingType) ? "묶음" : draft.BillingType;
        EditBillingAdvanceMode = string.IsNullOrWhiteSpace(draft.BillingAdvanceMode) ? "후불" : draft.BillingAdvanceMode;
        EditOfficeCode = draft.OfficeCode ?? EditOfficeCode;
        EditBillingMethod = draft.BillingMethod ?? string.Empty;
        EditPaymentMethod = draft.PaymentMethod ?? string.Empty;
        EditBillingStatus = string.IsNullOrWhiteSpace(draft.BillingStatus) ? "예정" : draft.BillingStatus;
        EditSettlementStatus = string.IsNullOrWhiteSpace(draft.SettlementStatus) ? PaymentFlowConstants.SettlementStatusUnpaid : draft.SettlementStatus;
        EditCompletionStatus = string.IsNullOrWhiteSpace(draft.CompletionStatus) ? PaymentFlowConstants.CompletionPending : draft.CompletionStatus;
        EditEmail = draft.Email ?? string.Empty;
        EditBillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(draft.BillingDayMode);
        EditBillingDay = RentalBillingScheduleRules.NormalizeBillingDay(draft.BillingDay);
        EditBillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(draft.BillingCycleMonths);
        EditBillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            EditBillingCycleMonths,
            draft.BillingAnchorMonth,
            ToDateOnly(draft.BillingAnchorDate),
            ToDateOnly(draft.BillingStartDate),
            ToDateOnly(draft.ContractStartDate),
            ToDateOnly(draft.ContractDate),
            ToDateOnly(draft.LastBilledDate),
            DateOnly.FromDateTime(DateTime.Today));
        EditDocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(draft.DocumentIssueMode);
        EditDocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(draft.DocumentLeadDays);
        EditMonthlyAmount = draft.MonthlyAmount;
        EditDepositAmount = draft.DepositAmount;
        EditSettledAmount = draft.SettledAmount;
        EditOutstandingAmount = draft.OutstandingAmount;
        EditRequiresFollowUp = draft.RequiresFollowUp;
        EditFollowUpNote = draft.FollowUpNote ?? string.Empty;
        EditSubmissionDocuments = draft.SubmissionDocuments ?? string.Empty;
        EditNotes = draft.Notes ?? string.Empty;
        LinkAssetsLater = draft.LinkAssetsLater;
        EditBillingAnchorDate = draft.BillingAnchorDate;
        EditBillingStartDate = draft.BillingStartDate;
        EditContractDate = draft.ContractDate;
        EditContractStartDate = draft.ContractStartDate;
        EditContractEndDate = draft.ContractEndDate;
        EditLastBilledDate = draft.LastBilledDate;
        EditLastSettledDate = draft.LastSettledDate;
        EditIsActive = draft.IsActive;

        TemplateItems.Clear();
        foreach (var item in draft.TemplateItems)
        {
            var editorItem = new RentalBillingTemplateEditorItem
            {
                ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                DisplayItemName = item.DisplayItemName,
                BillingLineMode = string.Equals((EditBillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                    ? NormalizeBillingLineModeValue(item.BillingLineMode)
                    : NormalizeBillingLineModeValue(EditBillingType),
                Quantity = item.Quantity,
                UnitPrice = item.UnitPrice,
                Amount = item.Amount,
                Note = item.Note
            };

            foreach (var assetId in item.IncludedAssetIds.Distinct())
                editorItem.IncludedAssetIds.Add(assetId);

            TemplateItems.Add(editorItem);
        }

        if (TemplateItems.Count == 0)
            TemplateItems.Add(CreateDefaultTemplateItem());

        SelectedTemplateItem = TemplateItems.FirstOrDefault(item => item.ItemId == draft.SelectedTemplateItemId)
            ?? TemplateItems.FirstOrDefault();
        UpdateTemplateDerivedValues();
    }

    private void PersistDraftBeforeContextSwitch()
    {
        if (IsAutoSaveSuppressed || !HasMeaningfulDraftState())
            return;

        var snapshot = BuildBillingEditorDraft();
        _ = SafePersistSnapshotAsync(snapshot);
    }

    private async Task SafePersistSnapshotAsync(RentalBillingEditorDraftModel snapshot)
    {
        try
        {
            await _rental.SaveBillingEditorDraftAsync(snapshot, _session);
        }
        catch
        {
        }
    }

    private bool HasMeaningfulDraftState()
    {
        if (!string.IsNullOrWhiteSpace(EditCustomerName) ||
            !string.IsNullOrWhiteSpace(EditBusinessNumber) ||
            !string.IsNullOrWhiteSpace(EditRealCustomerName) ||
            !string.IsNullOrWhiteSpace(EditBillToCustomerName) ||
            !string.IsNullOrWhiteSpace(EditInstallSiteName) ||
            !string.IsNullOrWhiteSpace(EditItemName) ||
            !string.IsNullOrWhiteSpace(EditBillingMethod) ||
            !string.IsNullOrWhiteSpace(EditPaymentMethod) ||
            !string.IsNullOrWhiteSpace(EditEmail) ||
            !string.IsNullOrWhiteSpace(EditFollowUpNote) ||
            !string.IsNullOrWhiteSpace(EditSubmissionDocuments) ||
            !string.IsNullOrWhiteSpace(EditNotes))
        {
            return true;
        }

        if (LinkAssetsLater ||
            !string.Equals(EditBillingDayMode, RentalBillingScheduleRules.BillingDayModeFixedDay, StringComparison.Ordinal) ||
            EditBillingDay != 25 ||
            EditBillingCycleMonths != 1 ||
            EditBillingAnchorMonth != 3 ||
            !string.Equals(EditDocumentIssueMode, RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate, StringComparison.Ordinal) ||
            EditDocumentLeadDays != 0 ||
            EditMonthlyAmount != 0m ||
            EditDepositAmount != 0m ||
            EditSettledAmount != 0m ||
            EditRequiresFollowUp)
        {
            return true;
        }

        if (!string.Equals(EditBillingType, "묶음", StringComparison.Ordinal) ||
            !string.Equals(EditBillingAdvanceMode, "후불", StringComparison.Ordinal) ||
            !string.Equals(EditBillingStatus, "예정", StringComparison.Ordinal) ||
            !string.Equals(EditSettlementStatus, PaymentFlowConstants.SettlementStatusUnpaid, StringComparison.Ordinal) ||
            !string.Equals(EditCompletionStatus, PaymentFlowConstants.CompletionPending, StringComparison.Ordinal) ||
            !EditIsActive)
        {
            return true;
        }

        if (EditBillingAnchorDate.HasValue || EditContractDate.HasValue || EditContractStartDate.HasValue || EditContractEndDate.HasValue || EditLastBilledDate.HasValue || EditLastSettledDate.HasValue)
            return true;

        if (EditBillingStartDate.HasValue && EditBillingStartDate.Value.Date != DateTime.Today.Date)
            return true;

        if (TemplateItems.Count != 1)
            return true;

        var templateItem = TemplateItems.FirstOrDefault();
        if (templateItem is null)
            return false;

        return !string.Equals(templateItem.DisplayItemName?.Trim(), "렌탈 임대료", StringComparison.Ordinal) ||
               templateItem.Quantity != 1m ||
               templateItem.UnitPrice != 0m ||
               templateItem.Amount != 0m ||
               !string.IsNullOrWhiteSpace(templateItem.Note) ||
               templateItem.IncludedAssetIds.Count > 0;
    }
}
