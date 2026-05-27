using System.Globalization;
using System.Net;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using Microsoft.Maui.Controls.Shapes;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Pages;

public sealed class ItemEditPage : ContentPage
{
    private readonly GeoraePlanApiClient _api;
    private readonly SessionStore _sessionStore;
    private ItemDto? _source;
    private readonly Func<ItemDto?, Task> _afterSaved;

    private readonly Entry _nameEntry;
    private readonly Entry _specEntry;
    private readonly Entry _categoryEntry;
    private readonly Entry _unitEntry;
    private readonly Entry _currentStockEntry;
    private readonly Entry _safetyStockEntry;
    private readonly Entry _purchasePriceEntry;
    private readonly Entry _salePriceEntry;
    private readonly Entry _retailPriceEntry;
    private readonly Editor _memoEditor;
    private readonly Label _statusLabel;
    private bool _isBusy;

    public ItemEditPage(ItemDto? item, string? selectedCategoryName, Func<ItemDto?, Task> afterSaved)
    {
        _api = ServiceHelper.GetRequiredService<GeoraePlanApiClient>();
        _sessionStore = ServiceHelper.GetRequiredService<SessionStore>();
        _source = item;
        _afterSaved = afterSaved;

        var isEdit = item is not null;
        GeoraePlanTheme.ApplyPage(this, isEdit ? "품목 수정" : "품목 신규등록");

        var category = string.IsNullOrWhiteSpace(item?.CategoryName)
            ? string.IsNullOrWhiteSpace(selectedCategoryName) ? "기타" : selectedCategoryName!.Trim()
            : item!.CategoryName;

        _nameEntry = CreateFormEntry("품명", item?.NameOriginal);
        _specEntry = CreateFormEntry("규격", item?.SpecificationOriginal);
        _categoryEntry = CreateFormEntry("품목분류", category);
        _unitEntry = CreateFormEntry("단위 예: EA / 개 / 대", string.IsNullOrWhiteSpace(item?.Unit) ? "EA" : item!.Unit);
        _currentStockEntry = CreateNumericEntry("현재재고", item?.CurrentStock ?? 0);
        _safetyStockEntry = CreateNumericEntry("안전재고", item?.SafetyStock ?? 0);
        _purchasePriceEntry = CreateNumericEntry("매입단가", item?.PurchasePrice ?? 0);
        _salePriceEntry = CreateNumericEntry("매출단가", item?.SalePrice ?? 0);
        _retailPriceEntry = CreateNumericEntry("소매단가", item?.RetailPrice ?? 0);
        _memoEditor = GeoraePlanTheme.CreateCompactEditor("메모", 86);
        _memoEditor.Text = item?.SimpleMemo ?? string.Empty;

        _statusLabel = GeoraePlanTheme.CreateStatusLabel();
        _statusLabel.Text = isEdit
            ? "수정 후 저장하면 PC와 모바일 품목 목록에 함께 반영됩니다."
            : "필수 항목은 품명입니다. 재고/단가는 필요 시 0으로 둘 수 있습니다.";

        var title = GeoraePlanTheme.CreateSectionTitle(isEdit ? "품목 정보 수정" : "새 품목 등록", 18);
        var guide = GeoraePlanTheme.CreateBodyText("모바일에서는 전표 입력에 필요한 기본 품목 정보와 단가를 빠르게 등록합니다.", true, 12);
        guide.LineHeight = 1.0;

        var saveButton = GeoraePlanTheme.CreateButton("저장", GeoraePlanTheme.Success);
        saveButton.Clicked += (_, _) => MobileErrorHandler.FireAndForget(SaveAsync, "품목 저장");

        var cancelButton = GeoraePlanTheme.CreateButton("취소", GeoraePlanTheme.SecondaryButton);
        cancelButton.Clicked += (_, _) => MobileErrorHandler.FireAndForget(CloseAsync, "품목 편집 닫기");

        var deleteButton = GeoraePlanTheme.CreateButton("삭제", GeoraePlanTheme.Danger);
        deleteButton.IsVisible = isEdit;
        deleteButton.Clicked += (_, _) => MobileErrorHandler.FireAndForget(DeleteAsync, "품목 삭제");

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

        var priceGrid = new Grid
        {
            ColumnSpacing = 8,
            RowSpacing = 8,
            ColumnDefinitions =
            {
                new ColumnDefinition(GridLength.Star),
                new ColumnDefinition(GridLength.Star)
            },
            RowDefinitions =
            {
                new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            }
        };
        priceGrid.Add(CreateField("매입단가", _purchasePriceEntry), 0, 0);
        priceGrid.Add(CreateField("매출단가", _salePriceEntry), 1, 0);
        priceGrid.Add(CreateField("소매단가", _retailPriceEntry), 0, 1);
        priceGrid.Add(CreateField("안전재고", _safetyStockEntry), 1, 1);

        var form = new VerticalStackLayout
        {
            Spacing = 8,
            Children =
            {
                title,
                guide,
                CreateField("품명", _nameEntry),
                CreateField("규격", _specEntry),
                CreateField("품목분류", _categoryEntry),
                CreateField("단위", _unitEntry),
                CreateField("현재재고", _currentStockEntry),
                priceGrid,
                CreateField("메모", _memoEditor),
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

    private static Entry CreateFormEntry(string placeholder, string? value = null)
    {
        var entry = GeoraePlanTheme.CreateCompactEntry(placeholder);
        entry.Text = value ?? string.Empty;
        return entry;
    }

    private static Entry CreateNumericEntry(string placeholder, decimal value)
    {
        var entry = CreateFormEntry(placeholder, value == 0 ? "0" : value.ToString("0.##", CultureInfo.InvariantCulture));
        entry.Keyboard = Keyboard.Numeric;
        entry.HorizontalTextAlignment = TextAlignment.End;
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

        var name = Read(_nameEntry);
        if (string.IsNullOrWhiteSpace(name))
        {
            await DisplayAlert("확인", "품명을 입력하세요.", "확인");
            return;
        }

        if (!TryResolveRequiredSessionScope(out var tenantCode, out var officeCode, out var scopeMessage))
        {
            _statusLabel.Text = scopeMessage;
            await DisplayAlert("저장 범위 확인", scopeMessage, "확인");
            return;
        }

        try
        {
            _isBusy = true;
            _statusLabel.Text = "품목을 저장하고 있습니다.";

            var dto = BuildDto(name, tenantCode, officeCode);
            var saved = _source is null
                ? await _api.CreateItemAsync(dto)
                : await _api.UpdateItemAsync(dto);

            _statusLabel.Text = "품목 저장 완료";
            await _afterSaved(saved);
            await CloseAsync();
        }
        catch (HttpRequestException ex) when (IsConcurrencyConflict(ex) && _source is not null)
        {
            await HandleConcurrencyConflictAsync("품목 저장");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"품목 저장 실패: {ex.Message}";
            await DisplayAlert("품목 저장 실패", ex.Message, "확인");
        }
        finally
        {
            _isBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_isBusy || _source is null)
            return;

        var confirm = await DisplayAlert("품목 삭제", $"'{_source.NameOriginal}' 품목을 삭제할까요?", "삭제", "취소");
        if (!confirm)
            return;

        try
        {
            _isBusy = true;
            _statusLabel.Text = "품목을 삭제하고 있습니다.";
            await _api.DeleteItemAsync(_source.Id, _source.Revision);
            await _afterSaved(null);
            await CloseAsync();
        }
        catch (HttpRequestException ex) when (IsConcurrencyConflict(ex) && _source is not null)
        {
            await HandleConcurrencyConflictAsync("품목 삭제");
        }
        catch (Exception ex)
        {
            _statusLabel.Text = $"품목 삭제 실패: {ex.Message}";
            await DisplayAlert("품목 삭제 실패", ex.Message, "확인");
        }
        finally
        {
            _isBusy = false;
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
            var latest = (await _api.GetItemDetailAsync(source.Id))?.Item;
            if (latest is null || latest.IsDeleted)
            {
                _statusLabel.Text = "다른 PC/모바일에서 해당 품목이 먼저 삭제되었습니다.";
                await DisplayAlert(
                    "동시 수정 충돌",
                    "다른 PC/모바일에서 해당 품목이 먼저 삭제되었습니다. 목록을 새로고침합니다.",
                    "확인");
                await _afterSaved(null);
                await CloseAsync();
                return;
            }

            _source = latest;
            ApplyItemToForm(latest);
            _statusLabel.Text = "최신 품목 정보를 다시 불러왔습니다. 내용을 확인한 뒤 다시 저장해 주세요.";
            await DisplayAlert(
                "동시 수정 충돌",
                $"{actionName} 중 다른 PC/모바일에서 먼저 저장된 최신 내용이 확인되었습니다.\n\n최신값을 화면에 다시 불러왔으니 내용을 확인한 뒤 다시 저장해 주세요.",
                "확인");
        }
        catch (Exception refreshEx)
        {
            _statusLabel.Text = $"최신 품목 정보를 다시 불러오지 못했습니다: {refreshEx.Message}";
            await DisplayAlert(
                "동시 수정 충돌",
                $"{actionName} 중 다른 PC/모바일에서 먼저 저장된 내용이 있습니다.\n최신값을 다시 불러오지 못했으므로 목록에서 새로고침 후 다시 시도해 주세요.\n\n{refreshEx.Message}",
                "확인");
        }
    }

    private void ApplyItemToForm(ItemDto item)
    {
        _nameEntry.Text = item.NameOriginal ?? string.Empty;
        _specEntry.Text = item.SpecificationOriginal ?? string.Empty;
        _categoryEntry.Text = string.IsNullOrWhiteSpace(item.CategoryName) ? "기타" : item.CategoryName;
        _unitEntry.Text = string.IsNullOrWhiteSpace(item.Unit) ? "EA" : item.Unit;
        _currentStockEntry.Text = FormatDecimal(item.CurrentStock);
        _safetyStockEntry.Text = FormatDecimal(item.SafetyStock);
        _purchasePriceEntry.Text = FormatDecimal(item.PurchasePrice);
        _salePriceEntry.Text = FormatDecimal(item.SalePrice);
        _retailPriceEntry.Text = FormatDecimal(item.RetailPrice);
        _memoEditor.Text = item.SimpleMemo ?? string.Empty;
    }

    private ItemDto BuildDto(string name, string tenantCode, string officeCode)
    {
        var dto = _source is null ? new ItemDto() : Clone(_source);
        dto.Id = _source?.Id ?? Guid.Empty;
        dto.TenantCode = string.IsNullOrWhiteSpace(dto.TenantCode) ? tenantCode : dto.TenantCode;
        dto.OfficeCode = string.IsNullOrWhiteSpace(dto.OfficeCode) ? officeCode : dto.OfficeCode;
        dto.NameOriginal = name;
        dto.NameMatchKey = string.Empty;
        dto.SpecificationOriginal = Read(_specEntry);
        dto.SpecificationMatchKey = string.Empty;
        dto.CategoryName = DefaultIfBlank(Read(_categoryEntry), "기타");
        dto.Unit = DefaultIfBlank(Read(_unitEntry), "EA");
        dto.CurrentStock = ReadDecimal(_currentStockEntry);
        dto.SafetyStock = ReadDecimal(_safetyStockEntry);
        dto.PurchasePrice = ReadDecimal(_purchasePriceEntry);
        dto.SalePrice = ReadDecimal(_salePriceEntry);
        dto.RetailPrice = ReadDecimal(_retailPriceEntry);
        dto.ItemKind = string.IsNullOrWhiteSpace(dto.ItemKind) ? ItemKinds.Product : dto.ItemKind;
        dto.TrackingType = string.IsNullOrWhiteSpace(dto.TrackingType) ? ItemTrackingTypes.Stock : dto.TrackingType;
        dto.IsSale = _source?.IsSale ?? true;
        dto.SimpleMemo = _memoEditor.Text?.Trim() ?? string.Empty;
        dto.Notes = _source?.Notes ?? string.Empty;
        dto.ExpectedRevision = _source?.Revision ?? 0;
        return dto;
    }

    private static ItemDto Clone(ItemDto source)
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
            TenantCode = source.TenantCode,
            OfficeCode = source.OfficeCode,
            NameOriginal = source.NameOriginal,
            NameMatchKey = source.NameMatchKey,
            SpecificationOriginal = source.SpecificationOriginal,
            SpecificationMatchKey = source.SpecificationMatchKey,
            CategoryName = source.CategoryName,
            ItemKind = source.ItemKind,
            TrackingType = source.TrackingType,
            Unit = source.Unit,
            CurrentStock = source.CurrentStock,
            SafetyStock = source.SafetyStock,
            PurchasePrice = source.PurchasePrice,
            SalePrice = source.SalePrice,
            RetailPrice = source.RetailPrice,
            PriceGradeA = source.PriceGradeA,
            PriceGradeB = source.PriceGradeB,
            PriceGradeC = source.PriceGradeC,
            SimpleMemo = source.SimpleMemo,
            IsRental = source.IsRental,
            IsSale = source.IsSale,
            SerialNumber = source.SerialNumber,
            MaterialNumber = source.MaterialNumber,
            InstallLocation = source.InstallLocation,
            RentalStartDate = source.RentalStartDate,
            RentalEndDate = source.RentalEndDate,
            Notes = source.Notes
        };

    private static string Read(Entry entry)
        => entry.Text?.Trim() ?? string.Empty;

    private static decimal ReadDecimal(Entry entry)
    {
        var text = Read(entry).Replace(",", string.Empty);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ||
               decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)
            ? parsed
            : 0;
    }

    private static string DefaultIfBlank(string value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatDecimal(decimal value)
        => value == 0 ? "0" : value.ToString("0.##", CultureInfo.InvariantCulture);

    private static bool IsConcurrencyConflict(HttpRequestException ex)
        => ex.StatusCode == HttpStatusCode.Conflict;

    private Task CloseAsync()
        => Navigation.PopModalAsync();
}
