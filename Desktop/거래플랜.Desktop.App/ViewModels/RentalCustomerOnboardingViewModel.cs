using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using 거래플랜.Desktop.App.Data;
using 거래플랜.Desktop.App.Services;
using 거래플랜.Shared.Contracts;

namespace 거래플랜.Desktop.App.ViewModels;

public sealed partial class RentalCustomerOnboardingViewModel : ObservableObject
{
    private readonly RentalStateService _rental;
    private readonly LocalStateService _local;
    private readonly SessionState _session;
    private List<LocalCustomer> _customers = new();

    [ObservableProperty] private int _currentStepIndex;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "신규 렌탈 거래처 등록을 시작하세요.";

    [ObservableProperty] private string _customerName = string.Empty;
    [ObservableProperty] private string _businessNumber = string.Empty;
    [ObservableProperty] private string _representative = string.Empty;
    [ObservableProperty] private string _contactPerson = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _address = string.Empty;

    [ObservableProperty] private string _officeCode = string.Empty;
    [ObservableProperty] private string _realCustomerName = string.Empty;
    [ObservableProperty] private string _billToCustomerName = string.Empty;
    [ObservableProperty] private string _installSiteName = string.Empty;

    [ObservableProperty] private string _billingType = "묶음";
    [ObservableProperty] private string _billingAdvanceMode = "후불";
    [ObservableProperty] private int _billingDay = 25;
    [ObservableProperty] private string _billingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
    [ObservableProperty] private int _billingCycleMonths = 1;
    [ObservableProperty] private int _billingAnchorMonth = 3;
    [ObservableProperty] private string _documentIssueMode = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
    [ObservableProperty] private int _documentLeadDays;
    [ObservableProperty] private DateTime _billingStartDate = DateTime.Today;
    [ObservableProperty] private decimal _monthlyAmount;
    [ObservableProperty] private string _billingMethod = string.Empty;
    [ObservableProperty] private string _paymentMethod = string.Empty;
    [ObservableProperty] private string _submissionDocuments = string.Empty;
    [ObservableProperty] private string _notes = string.Empty;
    [ObservableProperty] private bool _linkAssetsLater;
    [ObservableProperty] private RentalBillingTemplateEditorItem? _selectedTemplateItem;
    [ObservableProperty] private string _billingPreviewPeriod = string.Empty;
    [ObservableProperty] private string _expectedBillingAmountText = "0원";
    [ObservableProperty] private string _billingSchedulePreviewText = "청구일 규칙을 설정하면 다음 결제일이 표시됩니다.";
    [ObservableProperty] private string _documentIssuePreviewText = "서류 발송 규칙을 설정하면 예상 발송일이 표시됩니다.";
    [ObservableProperty] private string _applySelectedAssetsHint = "청구항목과 후보 장비를 선택하면 연결할 수 있습니다.";

    public ObservableCollection<DisplayOption> OfficeOptions { get; } = new();
    public ObservableCollection<string> BillingTypeOptions { get; } = new();
    public ObservableCollection<string> BillingLineModeOptions { get; } = new();
    public ObservableCollection<string> BillingAdvanceModeOptions { get; } = new();
    public ObservableCollection<string> BillingDayModeOptions { get; } = new();
    public ObservableCollection<int> BillingAnchorMonthOptions { get; } = new();
    public ObservableCollection<string> BillingMethodOptions { get; } = new();
    public ObservableCollection<string> DocumentIssueModeOptions { get; } = new();
    public ObservableCollection<string> CandidateAssetSummaryLines { get; } = new();
    public ObservableCollection<RentalBillingAssetOption> CandidateAssets { get; } = new();
    public ObservableCollection<RentalBillingTemplateEditorItem> TemplateItems { get; } = new();

    public event EventHandler? Completed;

    public Guid? SavedCustomerId { get; private set; }
    public Guid? SavedBillingProfileId { get; private set; }
    public bool IsCompleted { get; private set; }
    public string CurrentStepTitle => CurrentStepIndex switch
    {
        0 => "1. 거래처정보",
        1 => "2. 렌탈 기본정보",
        2 => "3. 임대료 청구 설정",
        3 => "4. 장비 연결",
        4 => "5. 청구항목 구성",
        _ => "6. 최종 확인"
    };
    public bool CanGoPrevious => CurrentStepIndex > 0 && !IsBusy;
    public bool CanGoNext => CurrentStepIndex < 5 && !IsBusy;
    public bool CanSave => CurrentStepIndex == 5 && !IsBusy;
    public bool CanRemoveTemplateItem => SelectedTemplateItem is not null;
    public bool CanApplySelectedAssets => SelectedTemplateItem is not null && CandidateAssets.Any(asset => asset.IsSelected);
    public bool IsFixedBillingDayMode => string.Equals(BillingDayMode, RentalBillingScheduleRules.BillingDayModeFixedDay, StringComparison.Ordinal);
    public bool IsDocumentLeadDaysVisible => string.Equals(DocumentIssueMode, RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate, StringComparison.Ordinal);

    public RentalCustomerOnboardingViewModel(RentalStateService rental, LocalStateService local, SessionState session)
    {
        _rental = rental;
        _local = local;
        _session = session;

        BillingTypeOptions.Add("묶음");
        BillingTypeOptions.Add("개별");
        BillingTypeOptions.Add("혼합");

        BillingLineModeOptions.Add("묶음");
        BillingLineModeOptions.Add("개별");

        BillingAdvanceModeOptions.Add("후불");
        BillingAdvanceModeOptions.Add("선불");

        BillingDayModeOptions.Add(RentalBillingScheduleRules.BillingDayModeFixedDay);
        BillingDayModeOptions.Add(RentalBillingScheduleRules.BillingDayModeEndOfMonth);

        for (var month = 1; month <= 12; month++)
            BillingAnchorMonthOptions.Add(month);

        BillingMethodOptions.Add("전자세금계산서");
        BillingMethodOptions.Add("CMS");
        BillingMethodOptions.Add("현금");
        BillingMethodOptions.Add("카드");

        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate);
        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate);
        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModePreviousBusinessDay);
        DocumentIssueModeOptions.Add(RentalBillingScheduleRules.DocumentIssueModePreviousMonthEnd);

        InitializeAutoSave();
    }

    partial void OnCurrentStepIndexChanged(int value)
    {
        OnPropertyChanged(nameof(CurrentStepTitle));
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        PreviousStepCommand.NotifyCanExecuteChanged();
        NextStepCommand.NotifyCanExecuteChanged();
        SaveCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedTemplateItemChanged(RentalBillingTemplateEditorItem? value)
    {
        SyncAssetSelectionFromTemplate();
        UpdateBillingPreview();
        RemoveTemplateItemCommand.NotifyCanExecuteChanged();
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
    }

    partial void OnCustomerNameChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(BillToCustomerName))
            BillToCustomerName = value;
        if (string.IsNullOrWhiteSpace(RealCustomerName))
            RealCustomerName = value;
        if (string.IsNullOrWhiteSpace(InstallSiteName))
            InstallSiteName = value;
    }

    partial void OnBillingDayChanged(int value) => UpdateBillingPreview();
    partial void OnBillingDayModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsFixedBillingDayMode));
        UpdateBillingPreview();
    }
    partial void OnBillingCycleMonthsChanged(int value)
    {
        BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(value);
        BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            BillingCycleMonths,
            BillingAnchorMonth,
            ToDateOnly(BillingStartDate),
            ToDateOnly(BillingStartDate),
            null,
            null,
            null,
            DateOnly.FromDateTime(DateTime.Today));
        UpdateBillingPreview();
    }
    partial void OnBillingAnchorMonthChanged(int value) => UpdateBillingPreview();
    partial void OnDocumentIssueModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsDocumentLeadDaysVisible));
        UpdateBillingPreview();
    }
    partial void OnDocumentLeadDaysChanged(int value) => UpdateBillingPreview();
    partial void OnBillingStartDateChanged(DateTime value) => UpdateBillingPreview();
    partial void OnBillingAdvanceModeChanged(string value) => UpdateBillingPreview();
    partial void OnBillingTypeChanged(string value)
    {
        if (string.Equals((value ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal))
        {
            UpdateTemplateTotals();
            return;
        }

        foreach (var item in TemplateItems)
            item.BillingLineMode = NormalizeBillingLineModeValue(value);

        UpdateTemplateTotals();
    }
    partial void OnMonthlyAmountChanged(decimal value)
    {
        if (TemplateItems.Count == 1)
        {
            var templateItem = TemplateItems[0];
            templateItem.UnitPrice = value;
            templateItem.Amount = value;
        }

        UpdateBillingPreview();
    }

    public async Task LoadAsync()
    {
        BeginAutoSaveSuppression();
        try
        {
            _customers = await _local.GetCustomersAsync(_session);
            var offices = await _local.GetOfficesAsync();
            OfficeOptions.Clear();
            foreach (var office in offices)
            {
                OfficeOptions.Add(new DisplayOption
                {
                    Value = office.Code,
                    DisplayName = office.Name
                });
            }

            OfficeCode = OfficeOptions.FirstOrDefault()?.Value
                ?? OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(_session.OfficeCode, DomainConstants.OfficeUsenet);
            BillingMethod = "전자세금계산서";
            PaymentMethod = "계좌이체";
            BillingDayMode = RentalBillingScheduleRules.BillingDayModeFixedDay;
            BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                BillingCycleMonths,
                BillingAnchorMonth,
                ToDateOnly(BillingStartDate),
                ToDateOnly(BillingStartDate),
                null,
                null,
                null,
                DateOnly.FromDateTime(DateTime.Today));
            DocumentIssueMode = RentalBillingScheduleRules.DocumentIssueModeSameAsDueDate;
            DocumentLeadDays = 0;
            TemplateItems.Clear();
            var defaultItem = CreateTemplateItem();
            TemplateItems.Add(defaultItem);
            SelectedTemplateItem = defaultItem;
            CurrentStepIndex = 0;
            StatusMessage = "신규 렌탈 거래처 등록을 시작하세요.";
            UpdateBillingPreview();
        }
        finally
        {
            EndAutoSaveSuppression();
        }

        await RestoreAutoSaveDraftAsync();
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private void PreviousStep()
    {
        if (CurrentStepIndex <= 0)
            return;

        CurrentStepIndex--;
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextStepAsync()
    {
        if (!ValidateCurrentStep())
            return;

        if (CurrentStepIndex == 3)
            await LoadCandidateAssetsAsync();

        if (CurrentStepIndex == 4)
            UpdateBillingPreview();

        if (CurrentStepIndex < 5)
            CurrentStepIndex++;
    }

    [RelayCommand]
    private void ApplyBillingCyclePreset(object? parameter)
    {
        if (!TryResolveBillingCycleMonths(parameter, out var months) || months <= 0)
            return;

        BillingCycleMonths = months;
        UpdateBillingPreview();
    }

    [RelayCommand]
    private async Task RefreshCandidateAssetsAsync()
        => await LoadCandidateAssetsAsync();

    [RelayCommand]
    private void AddTemplateItem()
    {
        var item = CreateTemplateItem();
        TemplateItems.Add(item);
        SelectedTemplateItem = item;
        UpdateTemplateTotals();
    }

    private static bool TryResolveBillingCycleMonths(object? parameter, out int months)
    {
        switch (parameter)
        {
            case int value:
                months = value;
                return true;
            case string text when int.TryParse(text, out var parsed):
                months = parsed;
                return true;
            default:
                months = 0;
                return false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveTemplateItem))]
    private void RemoveTemplateItem()
    {
        if (SelectedTemplateItem is null)
            return;

        var index = TemplateItems.IndexOf(SelectedTemplateItem);
        TemplateItems.Remove(SelectedTemplateItem);
        if (TemplateItems.Count == 0)
            TemplateItems.Add(CreateTemplateItem());
        SelectedTemplateItem = TemplateItems[Math.Clamp(index, 0, TemplateItems.Count - 1)];
        UpdateTemplateTotals();
    }

    [RelayCommand(CanExecute = nameof(CanApplySelectedAssets))]
    private void ApplySelectedAssetsToTemplate()
    {
        if (SelectedTemplateItem is null)
            return;

        SelectedTemplateItem.IncludedAssetIds.Clear();
        foreach (var assetId in CandidateAssets.Where(asset => asset.IsSelected).Select(asset => asset.AssetId).Distinct())
            SelectedTemplateItem.IncludedAssetIds.Add(assetId);
        SelectedTemplateItem.IncludedAssetSummary = BuildIncludedAssetSummary(SelectedTemplateItem.IncludedAssetIds);
        UpdateTemplateTotals();
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        if (!ValidateAllSteps())
            return;

        if (!TryValidateTemplateConfiguration(out var validationMessage))
        {
            StatusMessage = validationMessage;
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = "신규 렌탈 거래처를 저장하고 있습니다.";

            var existingCustomer = ResolveExistingCustomer();
            var customer = existingCustomer ?? new LocalCustomer
            {
                Id = Guid.NewGuid(),
                ResponsibleOfficeCode = OfficeCodeCatalog.NormalizeOfficeCodeOrDefault(OfficeCode, DomainConstants.OfficeUsenet),
                TenantCode = TenantScopeCatalog.NormalizeTenantCodeForOfficeOrDefault(null, OfficeCode),
                TradeType = CustomerTradeTypes.Sales,
                PriceGrade = "매출단가"
            };

            customer.NameOriginal = CustomerName.Trim();
            customer.NameMatchKey = RentalCatalogValueNormalizer.NormalizeLooseKey(customer.NameOriginal);
            customer.BusinessNumber = (BusinessNumber ?? string.Empty).Trim();
            customer.Representative = (Representative ?? string.Empty).Trim();
            customer.ContactPerson = (ContactPerson ?? string.Empty).Trim();
            customer.Phone = (Phone ?? string.Empty).Trim();
            customer.Email = (Email ?? string.Empty).Trim();
            customer.Address = (Address ?? string.Empty).Trim();
            customer.Department = (RealCustomerName ?? string.Empty).Trim();

            var customerResult = await _local.UpsertCustomerAsync(customer, _session);
            if (!customerResult.Success)
            {
                StatusMessage = customerResult.Message;
                return;
            }

            customer.Id = customerResult.EntityId;
            SavedCustomerId = customer.Id;

            UpdateTemplateTotals();
            var profile = new LocalRentalBillingProfile
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                CustomerName = CustomerName.Trim(),
                BusinessNumber = (BusinessNumber ?? string.Empty).Trim(),
                RealCustomerName = string.IsNullOrWhiteSpace(RealCustomerName) ? CustomerName.Trim() : RealCustomerName.Trim(),
                BillToCustomerName = string.IsNullOrWhiteSpace(BillToCustomerName) ? CustomerName.Trim() : BillToCustomerName.Trim(),
                InstallSiteName = string.IsNullOrWhiteSpace(InstallSiteName) ? (string.IsNullOrWhiteSpace(RealCustomerName) ? CustomerName.Trim() : RealCustomerName.Trim()) : InstallSiteName.Trim(),
                BillingType = BillingType,
                BillingAdvanceMode = BillingAdvanceMode,
                ManagementCompanyCode = OfficeCode,
                ResponsibleOfficeCode = OfficeCode,
                AssignedUsername = string.Empty,
                BillingMethod = BillingMethod,
                PaymentMethod = PaymentMethod,
                BillingStatus = "예정",
                SettlementStatus = PaymentFlowConstants.SettlementStatusUnpaid,
                CompletionStatus = PaymentFlowConstants.CompletionPending,
                BillingDay = BillingDay,
                BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(BillingDayMode),
                BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(BillingCycleMonths),
                BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                    BillingCycleMonths,
                    BillingAnchorMonth,
                    ToDateOnly(BillingStartDate),
                    ToDateOnly(BillingStartDate),
                    null,
                    null,
                    null,
                    DateOnly.FromDateTime(DateTime.Today)),
                DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(DocumentIssueMode),
                DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(DocumentLeadDays),
                BillingStartDate = DateOnly.FromDateTime(BillingStartDate),
                BillingAnchorDate = DateOnly.FromDateTime(BillingStartDate),
                MonthlyAmount = MonthlyAmount,
                SubmissionDocuments = SubmissionDocuments,
                Notes = Notes,
                Email = (Email ?? string.Empty).Trim(),
                BillingTemplateJson = _rental.SerializeBillingTemplateItems(ToTemplateModels())
            };

            var result = await _rental.SaveBillingProfileAsync(profile, _session);
            if (!result.Success)
            {
                StatusMessage = result.Message;
                return;
            }

            SavedBillingProfileId = result.EntityId;
            IsCompleted = true;
            await ClearAutoSaveDraftAsync();
            StatusMessage = existingCustomer is null
                ? "신규 렌탈 거래처 등록을 완료했습니다."
                : "기존 거래처에 렌탈 청구 설정을 추가했습니다.";
            Completed?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private LocalCustomer? ResolveExistingCustomer()
    {
        var trimmedBusinessNumber = (BusinessNumber ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(trimmedBusinessNumber))
        {
            var byBusinessNumber = _customers.FirstOrDefault(customer =>
                string.Equals((customer.BusinessNumber ?? string.Empty).Trim(), trimmedBusinessNumber, StringComparison.OrdinalIgnoreCase));
            if (byBusinessNumber is not null)
                return byBusinessNumber;
        }

        var trimmedName = CustomerName.Trim();
        return _customers.FirstOrDefault(customer =>
            string.Equals(customer.NameOriginal.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase));
    }

    private bool ValidateCurrentStep()
    {
        switch (CurrentStepIndex)
        {
            case 0:
                if (string.IsNullOrWhiteSpace(CustomerName))
                {
                    StatusMessage = "거래처명을 입력하세요.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(ContactPerson) && string.IsNullOrWhiteSpace(Phone))
                {
                    StatusMessage = "담당자명 또는 연락처를 입력하세요.";
                    return false;
                }
                return true;

            case 1:
                if (string.IsNullOrWhiteSpace(OfficeCode))
                {
                    StatusMessage = "담당지점을 선택하세요.";
                    return false;
                }
                if (string.IsNullOrWhiteSpace(BillToCustomerName))
                    BillToCustomerName = CustomerName.Trim();
                if (string.IsNullOrWhiteSpace(InstallSiteName))
                    InstallSiteName = string.IsNullOrWhiteSpace(RealCustomerName) ? CustomerName.Trim() : RealCustomerName.Trim();
                return true;

            case 2:
                BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(BillingDayMode);
                BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(BillingDay);
                BillingCycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(BillingCycleMonths);
                BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
                    BillingCycleMonths,
                    BillingAnchorMonth,
                    ToDateOnly(BillingStartDate),
                    ToDateOnly(BillingStartDate),
                    null,
                    null,
                    null,
                    DateOnly.FromDateTime(DateTime.Today));
                DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(DocumentIssueMode);
                DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(DocumentLeadDays);

                if (IsFixedBillingDayMode && (BillingDay <= 0 || BillingDay > 31))
                {
                    StatusMessage = "청구일은 1~31 사이여야 합니다.";
                    return false;
                }
                if (BillingCycleMonths <= 0)
                {
                    StatusMessage = "청구주기는 1개월 이상이어야 합니다.";
                    return false;
                }
                if (MonthlyAmount < 0m)
                {
                    StatusMessage = "월 임대료는 0원 이상이어야 합니다.";
                    return false;
                }
                return true;

            case 4:
                if (!TryValidateTemplateConfiguration(out var templateValidationMessage))
                {
                    StatusMessage = templateValidationMessage;
                    return false;
                }
                return true;

            default:
                return true;
        }
    }

    private bool ValidateAllSteps()
    {
        var previousStep = CurrentStepIndex;
        try
        {
            foreach (var step in new[] { 0, 1, 2, 4 })
            {
                CurrentStepIndex = step;
                if (!ValidateCurrentStep())
                    return false;
            }

            return true;
        }
        finally
        {
            CurrentStepIndex = previousStep;
        }
    }

    private async Task LoadCandidateAssetsAsync()
    {
        var assets = await _rental.GetBillingAssetCandidatesAsync(
            billingProfileId: null,
            customerName: CustomerName,
            billToCustomerName: BillToCustomerName,
            installSiteName: InstallSiteName,
            _session);

        CandidateAssets.Clear();
        CandidateAssetSummaryLines.Clear();
        foreach (var asset in assets)
        {
            var option = new RentalBillingAssetOption
            {
                AssetId = asset.Id,
                ManagementNumber = asset.ManagementNumber,
                ItemName = asset.ItemName,
                MachineNumber = asset.MachineNumber,
                CurrentCustomerName = string.IsNullOrWhiteSpace(asset.CurrentCustomerName) ? asset.CustomerName : asset.CurrentCustomerName,
                BillToCustomerName = string.IsNullOrWhiteSpace(asset.BillToCustomerName) ? asset.CustomerName : asset.BillToCustomerName,
                InstallSiteName = string.IsNullOrWhiteSpace(asset.InstallSiteName) ? asset.InstallLocation : asset.InstallSiteName,
                AssetStatus = asset.AssetStatus,
                BillingEligibilityStatus = string.IsNullOrWhiteSpace(asset.BillingEligibilityStatus) ? "미확인" : asset.BillingEligibilityStatus
            };
            option.PropertyChanged += (_, _) =>
            {
                ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanApplySelectedAssets));
                ApplySelectedAssetsHint = BuildApplySelectedAssetsHint();
            };
            CandidateAssets.Add(option);
        }

        if (!LinkAssetsLater &&
            SelectedTemplateItem is not null &&
            !SelectedTemplateItem.IncludedAssetIds.Any() &&
            assets.Count == 1)
        {
            SelectedTemplateItem.IncludedAssetIds.Add(assets[0].Id);
        }

        CandidateAssetSummaryLines.Add(assets.Count == 0
            ? "연결 가능한 장비를 찾지 못했습니다. 장비는 나중에 연결할 수 있습니다."
            : $"후보 장비 {assets.Count:N0}대를 찾았습니다.");
        UpdateTemplateTotals();
        SyncAssetSelectionFromTemplate();
    }

    private void SyncAssetSelectionFromTemplate()
    {
        if (SelectedTemplateItem is null)
        {
            foreach (var asset in CandidateAssets)
                asset.IsSelected = false;
            return;
        }

        var selectedIds = SelectedTemplateItem.IncludedAssetIds.ToHashSet();
        foreach (var asset in CandidateAssets)
            asset.IsSelected = selectedIds.Contains(asset.AssetId);
        ApplySelectedAssetsToTemplateCommand.NotifyCanExecuteChanged();
    }

    private RentalBillingTemplateEditorItem CreateTemplateItem()
    {
        var item = new RentalBillingTemplateEditorItem
        {
            DisplayItemName = "렌탈 임대료",
            BillingLineMode = string.Equals((BillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                ? string.Empty
                : NormalizeBillingLineModeValue(BillingType),
            Quantity = 1m,
            UnitPrice = MonthlyAmount,
            Amount = MonthlyAmount
        };
        item.PropertyChanged += (_, _) => UpdateTemplateTotals();
        return item;
    }

    private List<RentalBillingTemplateItemModel> ToTemplateModels()
        => TemplateItems.Select(item => new RentalBillingTemplateItemModel
        {
            ItemId = item.ItemId == Guid.Empty ? Guid.NewGuid() : item.ItemId,
            DisplayItemName = (item.DisplayItemName ?? string.Empty).Trim(),
            BillingLineMode = string.Equals((BillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal)
                ? NormalizeBillingLineModeValue(item.BillingLineMode)
                : NormalizeBillingLineModeValue(BillingType),
            Quantity = item.Quantity <= 0m ? 1m : item.Quantity,
            UnitPrice = Math.Max(0m, item.UnitPrice),
            Amount = item.Amount > 0m ? item.Amount : item.EffectiveAmount,
            Note = (item.Note ?? string.Empty).Trim(),
            IncludedAssetIds = item.IncludedAssetIds.Distinct().ToList()
        }).ToList();

    private bool TryValidateTemplateConfiguration(out string message)
    {
        message = string.Empty;
        if (TemplateItems.Count == 0)
        {
            message = "청구항목을 하나 이상 입력하세요.";
            return false;
        }

        if (TemplateItems.Any(item => string.IsNullOrWhiteSpace(item.DisplayItemName)))
        {
            message = "표시 품목명은 비워둘 수 없습니다.";
            return false;
        }

        if (string.Equals((BillingType ?? string.Empty).Trim(), "혼합", StringComparison.Ordinal))
        {
            if (TemplateItems.Any(item => string.IsNullOrWhiteSpace(NormalizeBillingLineModeValue(item.BillingLineMode))))
            {
                message = "혼합 청구는 모든 청구항목에 라인유형(묶음/개별)을 지정해야 합니다.";
                return false;
            }

            return true;
        }

        var normalizedMode = NormalizeBillingLineModeValue(BillingType);
        foreach (var item in TemplateItems)
            item.BillingLineMode = normalizedMode;

        return true;
    }

    private static string NormalizeBillingLineModeValue(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return string.Equals(trimmed, "개별", StringComparison.Ordinal)
            ? "개별"
            : string.Equals(trimmed, "묶음", StringComparison.Ordinal)
                ? "묶음"
                : string.Empty;
    }

    private void UpdateTemplateTotals()
    {
        foreach (var item in TemplateItems)
        {
            item.IncludedAssetSummary = BuildIncludedAssetSummary(item.IncludedAssetIds);
            if (item.Amount <= 0m)
                item.Amount = item.EffectiveAmount;
        }

        MonthlyAmount = TemplateItems.Sum(item => item.EffectiveAmount);
        UpdateBillingPreview();
    }

    private void UpdateBillingPreview()
    {
        var referenceDate = ToDateOnly(BillingStartDate) ?? DateOnly.FromDateTime(DateTime.Today);
        var cycleMonths = RentalBillingScheduleRules.NormalizeCycleMonths(BillingCycleMonths);
        BillingDayMode = RentalBillingScheduleRules.NormalizeBillingDayMode(BillingDayMode);
        BillingDay = RentalBillingScheduleRules.NormalizeBillingDay(BillingDay);
        BillingAnchorMonth = RentalBillingScheduleRules.NormalizeBillingAnchorMonth(
            cycleMonths,
            BillingAnchorMonth,
            ToDateOnly(BillingStartDate),
            ToDateOnly(BillingStartDate),
            null,
            null,
            null,
            referenceDate);
        DocumentIssueMode = RentalBillingScheduleRules.NormalizeDocumentIssueMode(DocumentIssueMode);
        DocumentLeadDays = RentalBillingScheduleRules.NormalizeDocumentLeadDays(DocumentLeadDays);

        var dueDate = RentalBillingScheduleRules.ResolveApplicableBillingDate(
            BillingDay,
            BillingDayMode,
            cycleMonths,
            BillingAnchorMonth,
            referenceDate,
            null);
        var period = RentalBillingScheduleRules.ResolveBillingPeriod(cycleMonths, BillingAdvanceMode, dueDate);
        var issueDate = RentalBillingScheduleRules.CalculateDocumentIssueDate(dueDate, DocumentIssueMode, DocumentLeadDays);
        var billingDayText = string.Equals(BillingDayMode, RentalBillingScheduleRules.BillingDayModeEndOfMonth, StringComparison.Ordinal)
            ? "말일"
            : $"매월 {BillingDay}일";
        var anchorText = cycleMonths == 1 ? "매월" : $"{BillingAnchorMonth}월 기준";

        BillingPreviewPeriod = period.StartDate == period.EndDate || (period.StartDate.Year == period.EndDate.Year && period.StartDate.Month == period.EndDate.Month)
            ? $"{period.StartDate:yyyy-MM}"
            : $"{period.StartDate:yyyy-MM} ~ {period.EndDate:yyyy-MM}";
        ExpectedBillingAmountText = $"{TemplateItems.Sum(item => item.EffectiveAmount) * cycleMonths:N0}원";
        BillingSchedulePreviewText = $"청구일 규칙: {billingDayText} / 기준월: {anchorText} / 예상 결제일: {dueDate:yyyy-MM-dd}";
        DocumentIssuePreviewText = issueDate.HasValue
            ? $"서류 발송 규칙: {BuildDocumentIssueModeText()} / 예상 발송일: {issueDate.Value:yyyy-MM-dd}"
            : "서류 발송일을 계산할 수 없습니다.";
        ApplySelectedAssetsHint = BuildApplySelectedAssetsHint();
        OnPropertyChanged(nameof(IsFixedBillingDayMode));
        OnPropertyChanged(nameof(IsDocumentLeadDaysVisible));
        OnPropertyChanged(nameof(CanApplySelectedAssets));
    }

    private string BuildDocumentIssueModeText()
        => DocumentIssueMode switch
        {
            RentalBillingScheduleRules.DocumentIssueModeDaysBeforeDueDate => $"결제일 {DocumentLeadDays}일 전",
            RentalBillingScheduleRules.DocumentIssueModePreviousBusinessDay => "결제일 직전 영업일",
            RentalBillingScheduleRules.DocumentIssueModePreviousMonthEnd => "전월 말일",
            _ => "결제일과 동일"
        };

    private string BuildApplySelectedAssetsHint()
    {
        if (SelectedTemplateItem is null)
            return "청구항목을 선택하면 장비 연결 안내가 표시됩니다.";

        var selectedCandidateCount = CandidateAssets.Count(asset => asset.IsSelected);
        if (SelectedTemplateItem.IncludedAssetIds.Count == 0 && CandidateAssets.Count > 0 && selectedCandidateCount == 0)
            return $"후보 장비 {CandidateAssets.Count:N0}대가 있습니다. 아래 후보 장비를 체크한 뒤 현재 품목에 연결하세요.";

        if (selectedCandidateCount == 0)
            return "후보 장비를 체크한 뒤 현재 품목에 연결하세요.";

        return $"선택한 장비 {selectedCandidateCount:N0}대를 현재 품목에 연결할 수 있습니다.";
    }

    private static DateOnly? ToDateOnly(DateTime? value)
        => value.HasValue ? DateOnly.FromDateTime(value.Value) : null;

    private string BuildIncludedAssetSummary(IEnumerable<Guid> assetIds)
    {
        var ids = assetIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0)
            return LinkAssetsLater ? "장비 나중 연결" : "연결 장비 없음";

        var labels = CandidateAssets
            .Where(asset => ids.Contains(asset.AssetId))
            .Select(asset => string.IsNullOrWhiteSpace(asset.ManagementNumber)
                ? asset.ItemName
                : $"{asset.ManagementNumber} {asset.ItemName}".Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Take(3)
            .ToList();

        return labels.Count == 0
            ? $"{ids.Count:N0}대 연결"
            : ids.Count > labels.Count
                ? $"{string.Join(", ", labels)} 외 {ids.Count - labels.Count}대"
                : string.Join(", ", labels);
    }
}
