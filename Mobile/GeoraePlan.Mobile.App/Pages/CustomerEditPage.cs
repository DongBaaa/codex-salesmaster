using System.Net;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class CustomerEditPage : ContentPage
{
    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;
    private readonly SyncCoordinator _syncCoordinator;
    private CustomerDto? _source;
    private readonly Func<CustomerDto?, Task> _afterSaved;

    private readonly Entry _nameEntry;
    private readonly Entry _phoneEntry;
    private readonly Entry _mobileEntry;
    private readonly Entry _businessNumberEntry;
    private readonly Entry _contactPersonEntry;
    private readonly Entry _tradeTypeEntry;
    private readonly Entry _priceGradeEntry;
    private readonly Entry _addressEntry;
    private readonly Editor _notesEditor;
    private readonly Label _statusLabel;
    private bool _isBusy;

    public CustomerEditPage(CustomerDto? customer, Func<CustomerDto?, Task> afterSaved)
    {
        _api = ServiceHelper.GetRequiredService<GeoraePlanApiClient>();
        _sessionStore = ServiceHelper.GetRequiredService<SessionStore>();
        _syncCoordinator = ServiceHelper.GetRequiredService<SyncCoordinator>();
        _source = customer;
        _afterSaved = afterSaved;

        var isEdit = customer is not null;
        GeoraePlanTheme.ApplyPage(this, isEdit ? "거래처 수정" : "거래처 신규등록");

        _nameEntry = CreateFormEntry("거래처명", customer?.NameOriginal);
        _phoneEntry = CreateFormEntry("대표전화", customer?.Phone, Keyboard.Telephone);
        _mobileEntry = CreateFormEntry("휴대폰", customer?.MobilePhone, Keyboard.Telephone);
        _businessNumberEntry = CreateFormEntry("사업자번호", customer?.BusinessNumber);
        _contactPersonEntry = CreateFormEntry("담당자", customer?.ContactPerson);
        _tradeTypeEntry = CreateFormEntry("거래구분 예: 매출 / 매입 / 매출매입", string.IsNullOrWhiteSpace(customer?.TradeType) ? "매출" : customer!.TradeType);
        _priceGradeEntry = CreateFormEntry("가격등급 예: 매출단가", string.IsNullOrWhiteSpace(customer?.PriceGrade) ? "매출단가" : customer!.PriceGrade);
        _addressEntry = CreateFormEntry("주소", customer?.Address);
        _notesEditor = GeoraePlanTheme.CreateCompactEditor("메모사항", 86);
        _notesEditor.Text = customer?.Notes ?? string.Empty;

        var canEditCustomers = _sessionStore.GetSnapshot().CanEditCustomers;

        _statusLabel = GeoraePlanTheme.CreateStatusLabel();
        _statusLabel.Text = !canEditCustomers
            ? "권한이 없어 거래처를 저장/삭제할 수 없습니다."
            : isEdit
            ? "수정 후 저장하면 PC와 동일 서버 데이터에 반영됩니다."
            : "필수 항목은 거래처명입니다. 저장 후 PC/모바일에서 함께 조회됩니다.";

        var title = GeoraePlanTheme.CreateSectionTitle(isEdit ? "거래처 정보 수정" : "새 거래처 등록", 18);
        var guide = GeoraePlanTheme.CreateBodyText("업무에 자주 쓰는 기본 정보만 먼저 입력하고, 세부 정보는 PC에서도 이어서 보완할 수 있습니다.", true, 12);
        guide.LineHeight = 1.0;

        var saveButton = GeoraePlanTheme.CreateButton("저장", GeoraePlanTheme.Success);
        saveButton.IsEnabled = canEditCustomers;
        saveButton.Clicked += (_, _) => MobileErrorHandler.FireAndForget(SaveAsync, "거래처 저장");

        var cancelButton = GeoraePlanTheme.CreateButton("취소", GeoraePlanTheme.SecondaryButton);
        cancelButton.Clicked += (_, _) => MobileErrorHandler.FireAndForget(CloseAsync, "거래처 편집 닫기");

        var deleteButton = GeoraePlanTheme.CreateButton("삭제", GeoraePlanTheme.Danger);
        deleteButton.IsVisible = isEdit && canEditCustomers;
        deleteButton.IsEnabled = canEditCustomers;
        deleteButton.Clicked += (_, _) => MobileErrorHandler.FireAndForget(DeleteAsync, "거래처 삭제");

        var actionGrid = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            }
        };
        actionGrid.Add(saveButton, 0, 0);
        actionGrid.Add(cancelButton, 1, 0);
        actionGrid.Add(deleteButton, 2, 0);

        var form = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                title,
                guide,
                CreateField("거래처명", _nameEntry),
                CreateField("대표전화", _phoneEntry),
                CreateField("휴대폰", _mobileEntry),
                CreateField("사업자번호", _businessNumberEntry),
                CreateField("담당자", _contactPersonEntry),
                CreateField("거래구분", _tradeTypeEntry),
                CreateField("가격등급", _priceGradeEntry),
                CreateField("주소", _addressEntry),
                CreateField("메모사항", _notesEditor),
                _statusLabel,
                actionGrid
            }
        };

        Content = new ScrollView
        {
            Content = new Border
            {
                Margin = 12,
                Padding = 12,
                BackgroundColor = GeoraePlanTheme.SurfaceAlt,
                Stroke = GeoraePlanTheme.Border,
                StrokeShape = new RoundRectangle { CornerRadius = 14 },
                Content = form
            }
        };
    }

    private static Entry CreateFormEntry(string placeholder, string? value = null, Keyboard? keyboard = null)
    {
        var entry = GeoraePlanTheme.CreateCompactEntry(placeholder);
        entry.Text = value ?? string.Empty;
        if (keyboard is not null)
            entry.Keyboard = keyboard;
        return entry;
    }

    private static View CreateField(string label, View input)
        => new VerticalStackLayout
        {
            Spacing = 3,
            Children =
            {
                GeoraePlanTheme.CreateFieldLabel(label),
                input
            }
        };

    private async Task SaveAsync()
    {
        if (_isBusy)
            return;

        if (!_sessionStore.GetSnapshot().CanEditCustomers)
        {
            _statusLabel.Text = "권한이 없어 거래처를 저장할 수 없습니다.";
            await DisplayAlert("권한 확인", _statusLabel.Text, "확인");
            return;
        }

        var name = Read(_nameEntry);
        if (string.IsNullOrWhiteSpace(name))
        {
            await DisplayAlert("확인", "거래처명을 입력하세요.", "확인");
            return;
        }

        if (!TryResolveRequiredSessionScope(out var tenantCode, out var officeCode, out var scopeMessage))
        {
            _statusLabel.Text = scopeMessage;
            await DisplayAlert("저장 범위 확인", scopeMessage, "확인");
            return;
        }

        CustomerDto? dto = null;
        try
        {
            _isBusy = true;
            _statusLabel.Text = "거래처를 저장하고 있습니다.";

            dto = BuildDto(name, tenantCode, officeCode);
            if (!MobileSessionScopeFilter.CanAccessCustomer(_sessionStore.GetSnapshot(), dto))
            {
                _statusLabel.Text = "현재 로그인 담당지점/업체 범위 밖 거래처는 저장할 수 없습니다.";
                await DisplayAlert("저장 범위 확인", _statusLabel.Text, "확인");
                return;
            }

            var saved = _source is null
                ? await _api.CreateCustomerAsync(dto)
                : await _api.UpdateCustomerAsync(dto);
            saved = EnsureSavedResult(saved, "거래처 저장");

            _statusLabel.Text = "거래처 저장 완료";
            await _afterSaved(saved);
            await CloseAsync();
        }
        catch (HttpRequestException ex) when (IsConcurrencyConflict(ex) && _source is not null)
        {
            await HandleConcurrencyConflictAsync("거래처 저장");
        }
        catch (Exception ex) when (dto is not null && MobileRetryableNetworkFailure.IsRetryable(ex))
        {
            await QueuePendingSaveAsync(dto, ex);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"거래처 저장 실패: {ex.Message}";
            await DisplayAlert("거래처 저장 실패", ex.Message, "확인");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private static T EnsureSavedResult<T>(T? result, string operationName)
        where T : SyncEntityDto
        => result ?? throw new HttpRequestException($"{operationName} 응답이 비어 있어 서버 반영 여부를 확인할 수 없습니다.");

    private async Task DeleteAsync()
    {
        if (_isBusy || _source is null)
            return;

        if (!_sessionStore.GetSnapshot().CanEditCustomers)
        {
            _statusLabel.Text = "권한이 없어 거래처를 삭제할 수 없습니다.";
            await DisplayAlert("권한 확인", _statusLabel.Text, "확인");
            return;
        }

        if (!MobileSessionScopeFilter.CanAccessCustomer(_sessionStore.GetSnapshot(), _source))
        {
            _statusLabel.Text = "현재 로그인 담당지점/업체 범위 밖 거래처는 삭제할 수 없습니다.";
            await DisplayAlert("삭제 범위 확인", _statusLabel.Text, "확인");
            return;
        }

        var confirm = await DisplayAlert("거래처 삭제", $"'{_source.NameOriginal}' 거래처를 삭제할까요?", "삭제", "취소");
        if (!confirm)
            return;

        try
        {
            _isBusy = true;
            _statusLabel.Text = "거래처를 삭제하고 있습니다.";
            await _api.DeleteCustomerAsync(_source.Id, _source.Revision);
            await _afterSaved(null);
            await CloseAsync();
        }
        catch (HttpRequestException ex) when (IsConcurrencyConflict(ex) && _source is not null)
        {
            await HandleConcurrencyConflictAsync("거래처 삭제");
        }
        catch (Exception ex) when (MobileRetryableNetworkFailure.IsRetryable(ex) && _source is not null)
        {
            await QueuePendingDeleteAsync(_source, ex);
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"거래처 삭제 실패: {ex.Message}";
            await DisplayAlert("거래처 삭제 실패", ex.Message, "확인");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task QueuePendingSaveAsync(CustomerDto dto, Exception ex)
    {
        var reason = MobileRetryableNetworkFailure.ToPendingSyncMessage(ex);
        try
        {
            var state = await _syncCoordinator.QueueCustomerDraftAsync(dto, reason);
            _statusLabel.Text = $"거래처 저장 완료(동기화 대기 {state.PendingCustomerCount:N0}건)";
            await DisplayAlert(
                "거래처 저장 대기",
                $"네트워크/서버 응답 지연으로 거래처를 기기에 먼저 저장했습니다.\n동기화 화면에서 저장 대기 거래처 {state.PendingCustomerCount:N0}건을 확인할 수 있으며, 연결 복구 후 자동으로 서버에 반영됩니다.",
                "확인");
            await CloseAsync();
            MobileErrorHandler.FireAndForget(() => _afterSaved(dto), "거래처 저장 후 목록 새로고침");
        }
        catch (Exception queueEx)
        {
            _statusLabel.Text = $"거래처 기기 저장 실패: {queueEx.Message}";
            await DisplayAlert("거래처 저장 실패", $"서버 저장 실패 후 기기 저장도 완료하지 못했습니다.\n\n원인: {queueEx.Message}", "확인");
        }
    }

    private async Task QueuePendingDeleteAsync(CustomerDto source, Exception ex)
    {
        var dto = BuildDeletedDto(source);
        var reason = MobileRetryableNetworkFailure.ToPendingSyncMessage(ex);
        try
        {
            var state = await _syncCoordinator.QueueCustomerDraftAsync(dto, reason);
            _statusLabel.Text = $"거래처 삭제 완료(동기화 대기 {state.PendingCustomerCount:N0}건)";
            await DisplayAlert(
                "거래처 삭제 대기",
                $"네트워크/서버 응답 지연으로 삭제 요청을 기기에 먼저 저장했습니다.\n동기화 화면에서 저장 대기 거래처 {state.PendingCustomerCount:N0}건을 확인할 수 있으며, 연결 복구 후 자동으로 서버에 반영됩니다.",
                "확인");
            await CloseAsync();
            MobileErrorHandler.FireAndForget(() => _afterSaved(dto), "거래처 삭제 후 목록 새로고침");
        }
        catch (Exception queueEx)
        {
            _statusLabel.Text = $"거래처 삭제 대기 저장 실패: {queueEx.Message}";
            await DisplayAlert("거래처 삭제 실패", $"서버 삭제 실패 후 기기 저장도 완료하지 못했습니다.\n\n원인: {queueEx.Message}", "확인");
        }
    }

    private bool TryResolveRequiredSessionScope(out string tenantCode, out string officeCode, out string message)
    {
        var snapshot = _sessionStore.GetSnapshot();
        tenantCode = snapshot.TenantCode?.Trim() ?? string.Empty;
        officeCode = snapshot.OfficeCode?.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(tenantCode) && !string.IsNullOrWhiteSpace(officeCode))
        {
            message = string.Empty;
            return true;
        }

        message = "로그인 지점/회사 범위를 확인할 수 없어 저장을 중단했습니다. 다시 로그인한 뒤 저장하세요.";
        return false;
    }

    private async Task HandleConcurrencyConflictAsync(string actionName)
    {
        var source = _source;
        if (source is null)
            return;

        _statusLabel.Text = "다른 PC/모바일에서 먼저 수정되어 최신값을 다시 확인하고 있습니다.";

        try
        {
            var latest = await _api.GetCustomerByIdAsync(source.Id);
            if (latest is null || latest.IsDeleted)
            {
                _statusLabel.Text = "다른 PC/모바일에서 해당 거래처가 먼저 삭제되었습니다.";
                await DisplayAlert(
                    "동시 수정 충돌",
                    "다른 PC/모바일에서 해당 거래처가 먼저 삭제되었습니다. 목록을 새로고침합니다.",
                    "확인");
                await _afterSaved(null);
                await CloseAsync();
                return;
            }

            _source = latest;
            ApplyCustomerToForm(latest);
            _statusLabel.Text = "최신 거래처 정보를 다시 불러왔습니다. 내용을 확인한 뒤 다시 저장해 주세요.";
            await DisplayAlert(
                "동시 수정 충돌",
                $"{actionName} 중 다른 PC/모바일에서 먼저 저장된 최신 내용이 확인되었습니다.\n\n최신값을 화면에 다시 불러왔으니 내용을 확인한 뒤 다시 저장해 주세요.",
                "확인");
        }
        catch (Exception refreshEx)
        {
            _statusLabel.Text = $"최신 거래처 정보를 다시 불러오지 못했습니다: {refreshEx.Message}";
            await DisplayAlert(
                "동시 수정 충돌",
                $"{actionName} 중 다른 PC/모바일에서 먼저 저장된 내용이 있습니다.\n최신값을 다시 불러오지 못했으므로 목록에서 새로고침 후 다시 시도해 주세요.\n\n{refreshEx.Message}",
                "확인");
        }
    }

    private void ApplyCustomerToForm(CustomerDto customer)
    {
        _nameEntry.Text = customer.NameOriginal ?? string.Empty;
        _phoneEntry.Text = customer.Phone ?? string.Empty;
        _mobileEntry.Text = customer.MobilePhone ?? string.Empty;
        _businessNumberEntry.Text = customer.BusinessNumber ?? string.Empty;
        _contactPersonEntry.Text = customer.ContactPerson ?? string.Empty;
        _tradeTypeEntry.Text = string.IsNullOrWhiteSpace(customer.TradeType) ? "매출" : customer.TradeType;
        _priceGradeEntry.Text = string.IsNullOrWhiteSpace(customer.PriceGrade) ? "매출단가" : customer.PriceGrade;
        _addressEntry.Text = customer.Address ?? string.Empty;
        _notesEditor.Text = customer.Notes ?? string.Empty;
    }

    private CustomerDto BuildDto(string name, string tenantCode, string officeCode)
    {
        var dto = _source is null ? new CustomerDto() : Clone(_source);
        dto.Id = _source?.Id ?? Guid.NewGuid();
        dto.TenantCode = string.IsNullOrWhiteSpace(dto.TenantCode) ? tenantCode : dto.TenantCode;
        dto.OfficeCode = string.IsNullOrWhiteSpace(dto.OfficeCode) ? officeCode : dto.OfficeCode;
        dto.ResponsibleOfficeCode = string.IsNullOrWhiteSpace(dto.ResponsibleOfficeCode) ? officeCode : dto.ResponsibleOfficeCode;
        dto.NameOriginal = name;
        dto.NameMatchKey = string.Empty;
        dto.Phone = Read(_phoneEntry);
        dto.MobilePhone = Read(_mobileEntry);
        dto.BusinessNumber = Read(_businessNumberEntry);
        dto.ContactPerson = Read(_contactPersonEntry);
        dto.TradeType = DefaultIfBlank(Read(_tradeTypeEntry), "매출");
        dto.PriceGrade = DefaultIfBlank(Read(_priceGradeEntry), "매출단가");
        dto.Address = Read(_addressEntry);
        dto.Notes = _notesEditor.Text?.Trim() ?? string.Empty;
        dto.ExpectedRevision = _source?.Revision ?? 0;
        StampMutation(dto);
        return dto;
    }

    private static CustomerDto BuildDeletedDto(CustomerDto source)
    {
        var dto = Clone(source);
        dto.IsDeleted = true;
        dto.ExpectedRevision = source.Revision;
        StampMutation(dto);
        return dto;
    }

    private static CustomerDto Clone(CustomerDto source)
        => new()
        {
            Id = source.Id,
            IsDeleted = source.IsDeleted,
            CreatedAtUtc = source.CreatedAtUtc,
            UpdatedAtUtc = source.UpdatedAtUtc,
            Revision = source.Revision,
            ExpectedRevision = source.Revision,
            MutationId = source.MutationId,
            MutationCreatedAtUtc = source.MutationCreatedAtUtc,
            CustomerMasterId = source.CustomerMasterId,
            TenantCode = source.TenantCode,
            OfficeCode = source.OfficeCode,
            ResponsibleOfficeCode = source.ResponsibleOfficeCode,
            NameOriginal = source.NameOriginal,
            NameMatchKey = source.NameMatchKey,
            CategoryId = source.CategoryId,
            TradeType = source.TradeType,
            Department = source.Department,
            ContactPerson = source.ContactPerson,
            Representative = source.Representative,
            BusinessNumber = source.BusinessNumber,
            BusinessType = source.BusinessType,
            BusinessItem = source.BusinessItem,
            Address = source.Address,
            DetailAddress = source.DetailAddress,
            Phone = source.Phone,
            MobilePhone = source.MobilePhone,
            FaxNumber = source.FaxNumber,
            Email = source.Email,
            HomePage = source.HomePage,
            Recipient = source.Recipient,
            PriceGrade = source.PriceGrade,
            Notes = source.Notes
        };

    private static string Read(InputView input)
        => input switch
        {
            Entry entry => entry.Text?.Trim() ?? string.Empty,
            Editor editor => editor.Text?.Trim() ?? string.Empty,
            _ => string.Empty
        };

    private static string DefaultIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static void StampMutation(CustomerDto dto)
    {
        var now = DateTime.UtcNow;
        dto.CreatedAtUtc = dto.CreatedAtUtc == default ? now : dto.CreatedAtUtc;
        dto.UpdatedAtUtc = now;
        dto.MutationId = BuildMutationId("customer", dto.Id);
        dto.MutationCreatedAtUtc = now;
    }

    private static string BuildMutationId(string entityName, Guid entityId)
        => $"mobile:{entityName}:{entityId:N}:{Guid.NewGuid():N}";

    private static bool IsConcurrencyConflict(HttpRequestException ex)
        => ex.StatusCode == HttpStatusCode.Conflict;

    private Task CloseAsync()
        => Navigation.PopModalAsync();
}
