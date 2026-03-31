using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalCustomerOnboardingViewModel
{
    private readonly SemaphoreSlim _autoSaveGate = new(1, 1);
    private CancellationTokenSource? _autoSaveCts;
    private int _autoSaveSuppressionCount;

    private static readonly HashSet<string> TrackedAutoSaveProperties = new(StringComparer.Ordinal)
    {
        nameof(CurrentStepIndex),
        nameof(CustomerName),
        nameof(BusinessNumber),
        nameof(Representative),
        nameof(ContactPerson),
        nameof(Phone),
        nameof(Email),
        nameof(Address),
        nameof(OfficeCode),
        nameof(AssignedUsername),
        nameof(RealCustomerName),
        nameof(BillToCustomerName),
        nameof(InstallSiteName),
        nameof(BillingType),
        nameof(BillingAdvanceMode),
        nameof(BillingDay),
        nameof(BillingDayMode),
        nameof(BillingCycleMonths),
        nameof(BillingAnchorMonth),
        nameof(DocumentIssueMode),
        nameof(DocumentLeadDays),
        nameof(BillingStartDate),
        nameof(MonthlyAmount),
        nameof(BillingMethod),
        nameof(PaymentMethod),
        nameof(SubmissionDocuments),
        nameof(Notes),
        nameof(LinkAssetsLater)
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
        var draft = await _rental.GetOnboardingDraftAsync(_session);
        if (draft is null)
            return false;

        BeginAutoSaveSuppression();
        try
        {
            ApplyOnboardingDraft(draft);
        }
        finally
        {
            EndAutoSaveSuppression();
        }

        if (CurrentStepIndex >= 3 || TemplateItems.Any(item => item.IncludedAssetIds.Count > 0))
            await LoadCandidateAssetsAsync();

        StatusMessage = "자동저장된 신규 렌탈 거래처 등록 내역을 불러왔습니다.";
        return true;
    }

    public async Task FlushAutoSaveAsync(CancellationToken ct = default)
    {
        _autoSaveCts?.Cancel();
        await PersistAutoSaveDraftAsync(ct);
    }

    public async Task ClearAutoSaveDraftAsync(CancellationToken ct = default)
    {
        _autoSaveCts?.Cancel();
        await _rental.ClearOnboardingDraftAsync(_session, ct);
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
        if (IsAutoSaveSuppressed || IsCompleted)
            return;

        await _autoSaveGate.WaitAsync(ct);
        try
        {
            if (HasMeaningfulDraftState())
            {
                await _rental.SaveOnboardingDraftAsync(BuildOnboardingDraft(), _session, ct);
            }
            else
            {
                await _rental.ClearOnboardingDraftAsync(_session, ct);
            }
        }
        finally
        {
            _autoSaveGate.Release();
        }
    }

    private RentalCustomerOnboardingDraftModel BuildOnboardingDraft()
        => new()
        {
            CurrentStepIndex = CurrentStepIndex,
            CustomerName = CustomerName,
            BusinessNumber = BusinessNumber,
            Representative = Representative,
            ContactPerson = ContactPerson,
            Phone = Phone,
            Email = Email,
            Address = Address,
            OfficeCode = OfficeCode,
            AssignedUsername = AssignedUsername,
            RealCustomerName = RealCustomerName,
            BillToCustomerName = BillToCustomerName,
            InstallSiteName = InstallSiteName,
            BillingType = BillingType,
            BillingAdvanceMode = BillingAdvanceMode,
            BillingDay = BillingDay,
            BillingDayMode = BillingDayMode,
            BillingCycleMonths = BillingCycleMonths,
            BillingAnchorMonth = BillingAnchorMonth,
            DocumentIssueMode = DocumentIssueMode,
            DocumentLeadDays = DocumentLeadDays,
            BillingStartDate = BillingStartDate,
            MonthlyAmount = MonthlyAmount,
            BillingMethod = BillingMethod,
            PaymentMethod = PaymentMethod,
            SubmissionDocuments = SubmissionDocuments,
            Notes = Notes,
            LinkAssetsLater = LinkAssetsLater,
            SelectedTemplateItemId = SelectedTemplateItem?.ItemId,
            TemplateItems = ToTemplateModels()
        };

    private void ApplyOnboardingDraft(RentalCustomerOnboardingDraftModel draft)
    {
        CurrentStepIndex = Math.Clamp(draft.CurrentStepIndex, 0, 5);
        CustomerName = draft.CustomerName ?? string.Empty;
        BusinessNumber = draft.BusinessNumber ?? string.Empty;
        Representative = draft.Representative ?? string.Empty;
        ContactPerson = draft.ContactPerson ?? string.Empty;
        Phone = draft.Phone ?? string.Empty;
        Email = draft.Email ?? string.Empty;
        Address = draft.Address ?? string.Empty;
        OfficeCode = draft.OfficeCode ?? OfficeCode;
        AssignedUsername = draft.AssignedUsername ?? AssignedUsername;
        RealCustomerName = draft.RealCustomerName ?? string.Empty;
        BillToCustomerName = draft.BillToCustomerName ?? string.Empty;
        InstallSiteName = draft.InstallSiteName ?? string.Empty;
        BillingType = string.IsNullOrWhiteSpace(draft.BillingType) ? "묶음" : draft.BillingType;
        BillingAdvanceMode = string.IsNullOrWhiteSpace(draft.BillingAdvanceMode) ? "후불" : draft.BillingAdvanceMode;
        BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(draft.BillingDayMode);
        BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(draft.BillingDay);
        BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(draft.BillingCycleMonths);
        BillingStartDate = draft.BillingStartDate == default ? DateTime.Today : draft.BillingStartDate;
        BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            BillingCycleMonths,
            draft.BillingAnchorMonth,
            DateOnly.FromDateTime(BillingStartDate),
            DateOnly.FromDateTime(BillingStartDate),
            null,
            null,
            null,
            DateOnly.FromDateTime(BillingStartDate));
        DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(draft.DocumentIssueMode);
        DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(draft.DocumentLeadDays);
        MonthlyAmount = draft.MonthlyAmount;
        BillingMethod = draft.BillingMethod ?? BillingMethod;
        PaymentMethod = draft.PaymentMethod ?? PaymentMethod;
        SubmissionDocuments = draft.SubmissionDocuments ?? string.Empty;
        Notes = draft.Notes ?? string.Empty;
        LinkAssetsLater = draft.LinkAssetsLater;

        TemplateItems.Clear();
        foreach (var item in draft.TemplateItems)
        {
            var editorItem = new RentalBillingTemplateEditorItem
            {
                ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
                DisplayItemName = item.DisplayItemName,
                BillingLineMode = string.Equals((BillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                    ? NormalizeBillingLineModeValue(item.BillingLineMode)
                    : NormalizeBillingLineModeValue(BillingType),
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
            TemplateItems.Add(CreateTemplateItem());

        SelectedTemplateItem = TemplateItems.FirstOrDefault(item => item.ItemId == draft.SelectedTemplateItemId)
            ?? TemplateItems.FirstOrDefault();
        UpdateTemplateTotals();
    }

    private bool HasMeaningfulDraftState()
    {
        if (!string.IsNullOrWhiteSpace(CustomerName) ||
            !string.IsNullOrWhiteSpace(BusinessNumber) ||
            !string.IsNullOrWhiteSpace(Representative) ||
            !string.IsNullOrWhiteSpace(ContactPerson) ||
            !string.IsNullOrWhiteSpace(Phone) ||
            !string.IsNullOrWhiteSpace(Email) ||
            !string.IsNullOrWhiteSpace(Address) ||
            !string.IsNullOrWhiteSpace(RealCustomerName) ||
            !string.IsNullOrWhiteSpace(BillToCustomerName) ||
            !string.IsNullOrWhiteSpace(InstallSiteName) ||
            !string.IsNullOrWhiteSpace(SubmissionDocuments) ||
            !string.IsNullOrWhiteSpace(Notes))
        {
            return true;
        }

        if (CurrentStepIndex > 0 ||
            LinkAssetsLater ||
            !string.Equals(BillingDayMode, RentalBillingScheduleRules.BillingDayModeFixedDay, StringComparison.Ordinal) ||
            BillingDay != 25 ||
            BillingCycleMonths != 1 ||
            BillingAnchorMonth != 3 ||
            !string.Equals(DocumentIssueMode, RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate, StringComparison.Ordinal) ||
            DocumentLeadDays != 0 ||
            BillingStartDate.Date != DateTime.Today.Date)
        {
            return true;
        }

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
