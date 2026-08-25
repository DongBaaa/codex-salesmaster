#if GEORAEPLAN_MOBILE_UI_MATRIX
using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Android.Util;
using GeoraePlan.Mobile.App.Pages;
using GeoraePlan.Mobile.App.Services;
using GeoraePlan.Mobile.App.Theme;
using GeoraePlan.Mobile.App.ViewModels;
using Microsoft.Maui;
using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using SharedContracts = 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.UiMatrix;

internal sealed class MobileUiMatrixHostPage : ContentPage
{
    public const string RequestExtraName = "georaeplan.uiMatrix.request";
    private const string LogTag = "GeoraePlanUiMatrix";
    private const string ResultFileName = "ui-matrix-result.json";
    private const int MaximumRequestBytes = 131_072;
    private static readonly object PendingRequestSync = new();
    private static string? _pendingEncodedRequest;
    private static readonly Guid FixtureId =
        Guid.ParseExact("0123456789abcdef0123456789abcdef", "N");
    private readonly SessionStore _sessionStore;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private int _requestGeneration;

    public MobileUiMatrixHostPage(SessionStore sessionStore)
    {
        _sessionStore = sessionStore ??
            throw new ArgumentNullException(nameof(sessionStore));
        GeoraePlanTheme.ApplyPage(this, "UI Matrix");
        NavigationPage.SetHasNavigationBar(this, false);
        Content = new Grid
        {
            Children =
            {
                new Label
                {
                    Text = "UI matrix request pending",
                    HorizontalOptions = LayoutOptions.Center,
                    VerticalOptions = LayoutOptions.Center
                }
            }
        };
        string? pending;
        lock (PendingRequestSync)
        {
            pending = _pendingEncodedRequest;
            _pendingEncodedRequest = null;
        }
        if (!string.IsNullOrWhiteSpace(pending))
            LoadEncodedRequest(pending);
    }

    public static void DispatchEncodedRequest(string encoded)
    {
        lock (PendingRequestSync)
            _pendingEncodedRequest = encoded;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (Microsoft.Maui.Controls.Application.Current?.MainPage is
                    NavigationPage navigation &&
                navigation.RootPage is MobileUiMatrixHostPage host)
            {
                lock (PendingRequestSync)
                    _pendingEncodedRequest = null;
                host.LoadEncodedRequest(encoded);
            }
        });
    }

    public void LoadEncodedRequest(string encoded)
    {
        var generation = Interlocked.Increment(ref _requestGeneration);
        _ = LoadEncodedRequestAsync(encoded, generation);
    }

    private async Task LoadEncodedRequestAsync(
        string encoded,
        int generation)
    {
        await _loadGate.WaitAsync();
        try
        {
            var request = DecodeRequest(encoded);
            if (generation != Volatile.Read(ref _requestGeneration))
                return;

            var result = await ExecuteAsync(request);
            PublishResult(result);
        }
        catch (Exception ex)
        {
            PublishResult(new MobileUiMatrixResult
            {
                MeasurementId = TryReadMeasurementId(encoded),
                Passed = false,
                Errors = [$"{ex.GetType().Name}: {ex.Message}"]
            });
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private async Task<MobileUiMatrixResult> ExecuteAsync(
        MobileUiMatrixRequest request)
    {
        ValidateRequest(request);
        await EnsureAdminSessionAsync();
        MobileUiMatrixActionRegistry.Reset();

        var sourcePage = CreatePage(request.Page);
        var root = CreateStateRoot(sourcePage, request);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} root");
        sourcePage.Content = null;
        BindingContext = sourcePage.BindingContext;
        Content = root;

        SeedObjectCollections(sourcePage.BindingContext, 0);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} source-seeded");
        SeedObjectCollections(root.BindingContext, 0);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} root-seeded");
        ActivateStateOwner(sourcePage, request);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} activated");
        SeedVisualCollections(root);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} visual-seeded");
        InvalidateMeasure();
        await WaitForLayoutAsync(root);

        SeedVisualCollections(root);
        await WaitForLayoutAsync(root);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} layout");
        var naturalVisibility = EnumerateVisualElements(root)
            .ToDictionary(element => element, IsEffectivelyVisible);
        var extendedState = !string.Equals(
            request.StateRequirement,
            "initial-layout",
            StringComparison.Ordinal);
        if (extendedState)
        {
            PrepareExpectedActionState(root, request, includeItemTemplates: true);
            SeedVisualCollections(root);
            await RealizeItemTemplatesAsync(root);
            await WaitForLayoutAsync(root);
            Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} items");
        }
        var keyboardFocused = false;
        if (string.Equals(request.Keyboard, "open", StringComparison.Ordinal))
        {
            var input = EnumerateVisualElements(root)
                .FirstOrDefault(element =>
                    (element is Entry or Editor or SearchBar) &&
                    IsEffectivelyVisible(element));
            if (input is not null)
            {
                input.AutomationId = "GEORAEPLAN_UI_MATRIX_KEYBOARD_TARGET";
                keyboardFocused = input.Focus();
            }
        }

        await Task.Delay(
            string.Equals(request.Keyboard, "open", StringComparison.Ordinal)
                ? 650
                : 180);

        var textElementResults = MeasureVisibleTextElements(root);
        if (!extendedState)
        {
            PrepareExpectedActionState(root, request, includeItemTemplates: false);
            SeedVisualCollections(root);
            await RealizeItemTemplatesAsync(root);
            await WaitForLayoutAsync(root);
        }
        var actions = ResolveActions(root, request, naturalVisibility);
        InvalidateMeasure();
        await WaitForActionLayoutAsync(actions);
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} resolved");
        var actionResults = new List<MobileUiMatrixActionResult>();
        var initialActionBounds = actions
            .Select(action => GetViewportBounds(action.Element))
            .ToArray();
        for (var index = 0; index < actions.Count; index++)
            actionResults.Add(await MeasureActionAsync(actions[index], initialActionBounds[index]));
        Log.Info(LogTag, $"GEORAEPLAN_UI_MATRIX_PHASE_V1 {request.MeasurementId} measured");

        var errors = new List<string>();
        if (actions.Count != request.Actions.Count)
        {
            errors.Add(
                $"action-count expected={request.Actions.Count} actual={actions.Count}");
            var registered = MobileUiMatrixActionRegistry.Snapshot(request.Page);
            var allRegistered = MobileUiMatrixActionRegistry.SnapshotAll();
            errors.Add(
                $"action-registry page={registered.Count} tree={registered.Count(entry => IsDescendantOf(entry.Button, root))} total={allRegistered.Count} tree-lines={string.Join(',', registered.Where(entry => IsDescendantOf(entry.Button, root)).Select(entry => entry.SourceLine).OrderBy(line => line))}");
        }
        if (string.Equals(request.Keyboard, "open", StringComparison.Ordinal) &&
            !keyboardFocused)
        {
            errors.Add("keyboard target was not focused");
        }

        foreach (var action in actionResults)
        {
            if (!action.Visible)
                errors.Add($"action-not-visible:{action.StableId}");
            if (action.Width <= 0 || action.Height <= 0)
                errors.Add($"action-empty-bounds:{action.StableId}");
            if (!action.Reachable)
                errors.Add($"action-not-reachable:{action.StableId}");
            if (!action.TextFits)
                errors.Add($"action-text-clipped:{action.StableId}");
        }

        foreach (var textElement in textElementResults)
        {
            if (!textElement.Wraps)
                errors.Add($"text-element-nonwrapping:{textElement.StableId}");
            if (!textElement.TextFits)
                errors.Add(
                    $"text-element-clipped:{textElement.StableId}:{textElement.Diagnostic}");
        }

        for (var left = 0; left < actionResults.Count; left++)
        {
            if (!actionResults[left].NaturalState ||
                !actionResults[left].OnScreen)
                continue;
            for (var right = left + 1; right < actionResults.Count; right++)
            {
                if (actionResults[right].NaturalState &&
                    actionResults[right].OnScreen &&
                    IntersectsInitial(actionResults[left], actionResults[right]))
                {
                    errors.Add(
                        $"action-overlap:{actionResults[left].StableId}:{actionResults[right].StableId}");
                }
            }
        }

        return new MobileUiMatrixResult
        {
            MeasurementId = request.MeasurementId,
            Page = request.Page,
            StateRequirement = request.StateRequirement,
            Scenario = request.Scenario,
            Keyboard = request.Keyboard,
            ExpectedActionCount = request.Actions.Count,
            ActualActionCount = actions.Count,
            TextElementCount = textElementResults.Count,
            KeyboardFocused = keyboardFocused,
            ViewportWidth = Width,
            ViewportHeight = Height,
            Passed = errors.Count == 0,
            Errors = errors,
            Actions = actionResults
        };
    }

    private async Task EnsureAdminSessionAsync()
    {
        var current = _sessionStore.GetSnapshot();
        if (current.IsAuthenticated && current.IsAdmin)
            return;

        await _sessionStore.SaveAsync(new SharedContracts.LoginResponse
        {
            Token = "ui-matrix-isolated-token",
            ExpiresAtUtc = DateTime.UtcNow.AddHours(8),
            User = new SharedContracts.UserSessionDto
            {
                UserId = FixtureId,
                Username = "ui-matrix-admin",
                Role = "Admin",
                TenantCode = SharedContracts.TenantScopeCatalog.UsenetGroup,
                OfficeCode = string.Empty,
                ScopeType = SharedContracts.TenantScopeCatalog.ScopeAdmin,
                Permissions = []
            }
        });
    }

    private static ContentPage CreatePage(string page)
        => page switch
        {
            nameof(CustomerContractsPage) =>
                new CustomerContractsPage(FixtureId, "대표 거래처"),
            nameof(CustomerEditPage) =>
                new CustomerEditPage(null, (_, _) => Task.CompletedTask),
            nameof(CustomersPage) => new CustomersPage(),
            nameof(HomePage) => new HomePage(),
            nameof(IntegrityReportPage) => new IntegrityReportPage(),
            nameof(InventoryTransfersPage) => new InventoryTransfersPage(),
            nameof(InvoiceDraftPage) => new InvoiceDraftPage(),
            nameof(InvoicesPage) => new InvoicesPage(),
            nameof(ItemEditPage) =>
                new ItemEditPage(null, "대표 분류", (_, _) => Task.CompletedTask),
            nameof(ItemsPage) => new ItemsPage(),
            nameof(LoginPage) => new LoginPage(),
            nameof(PaymentAttachmentsPage) =>
                new PaymentAttachmentsPage(FixtureId, "대표 첨부"),
            nameof(PaymentDraftPage) => new PaymentDraftPage(),
            nameof(RecycleBinPage) => new RecycleBinPage(),
            nameof(RentalsPage) => new RentalsPage(),
            nameof(SettingsPage) => new SettingsPage(),
            nameof(SyncPage) => new SyncPage(),
            nameof(UpdateRequiredPage) => new UpdateRequiredPage(
                new MobileCompatibilityGateOutcome
                {
                    IsBlocked = true,
                    StatusMessage = "UI matrix",
                    Update = new MobileAppUpdateCheckResult
                    {
                        CurrentVersion = "0.0.0",
                        LatestVersion = "9.9.9",
                        IsUpdateAvailable = true,
                        IsServerEnforced = true,
                        Message = "UI matrix update requirement"
                    }
                },
                _ => Task.CompletedTask,
                _ => Task.CompletedTask),
            _ => throw new InvalidOperationException(
                $"Unsupported UI matrix page: {page}")
        };

    private static View CreateStateRoot(
        ContentPage page,
        MobileUiMatrixRequest request)
    {
        if (string.Equals(
                request.StateOwner,
                "CreateInlineCustomerDetailView",
                StringComparison.Ordinal))
        {
            var method = page.GetType().GetMethod(
                request.StateOwner,
                BindingFlags.Instance | BindingFlags.NonPublic);
            if (method?.Invoke(page, null) is not View detail)
                throw new InvalidOperationException("Customer detail state could not be created.");
            detail.BindingContext = page.BindingContext;
            return new ScrollView { Content = detail };
        }

        return page.Content ??
            throw new InvalidOperationException($"Page has no content: {request.Page}");
    }

    private static void ActivateStateOwner(
        ContentPage page,
        MobileUiMatrixRequest request)
    {
        if (!string.Equals(
                request.StateOwner,
                "RebuildRecentItems",
                StringComparison.Ordinal))
            return;

        var method = page.GetType().GetMethod(
            request.StateOwner,
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: Type.EmptyTypes,
            modifiers: null);
        if (method is null)
            throw new InvalidOperationException("Recent-item state activator is missing.");
        method.Invoke(page, null);
    }

    private static void SeedVisualCollections(View root)
    {
        foreach (var itemsView in EnumerateVisualElements(root).OfType<ItemsView>())
            TrySeedCollection(itemsView.ItemsSource);
    }

    private static void PrepareExpectedActionState(
        View root,
        MobileUiMatrixRequest request,
        bool includeItemTemplates)
    {
        var registered = MobileUiMatrixActionRegistry.Snapshot(request.Page);
        foreach (var expected in request.Actions.Where(action =>
                     string.Equals(action.Kind, "Button", StringComparison.Ordinal)))
        {
            var expectedLine = ReadStableLine(expected.StableId);
            var entry = registered
                .Where(candidate => candidate.SourceLine == expectedLine)
                .Where(candidate => string.IsNullOrWhiteSpace(expected.Label) ||
                                    string.Equals(candidate.Text, expected.Label, StringComparison.Ordinal))
                .FirstOrDefault();
            if (entry is not null && IsDescendantOf(entry.Button, root))
                ForceVisible(entry.Button);
        }

        if (!includeItemTemplates)
            return;
        foreach (var itemsView in EnumerateVisualElements(root).OfType<ItemsView>())
            ForceVisible(itemsView);
    }

    private static void SeedObjectCollections(object? owner, int depth)
    {
        if (owner is null || depth > 1)
            return;

        foreach (var property in owner.GetType().GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0 ||
                !property.CanRead)
                continue;
            object? value;
            try
            {
                value = property.GetValue(owner);
            }
            catch
            {
                continue;
            }

            if (TrySeedCollection(value))
                continue;
            if (value is not null &&
                property.PropertyType.Namespace?.StartsWith(
                    "GeoraePlan.Mobile.App.ViewModels",
                    StringComparison.Ordinal) == true)
            {
                SeedObjectCollections(value, depth + 1);
            }
        }
    }

    private static bool TrySeedCollection(object? candidate)
    {
        if (candidate is not IList list || list.IsReadOnly || list.IsFixedSize)
            return false;
        if (list.Count > 0)
            return true;

        var elementType = candidate.GetType().GetInterfaces()
            .Concat([candidate.GetType()])
            .Where(type => type.IsGenericType)
            .Where(type => type.GetGenericTypeDefinition() is var definition &&
                           (definition == typeof(IList<>) ||
                            definition == typeof(ICollection<>)))
            .Select(type => type.GetGenericArguments()[0])
            .FirstOrDefault();
        if (elementType is null || elementType == typeof(string))
            return false;

        var fixture = CreateFixture(elementType, 0);
        if (fixture is null)
            return false;
        try
        {
            list.Add(fixture);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static object? CreateFixture(Type type, int depth)
    {
        if (type == typeof(InvoiceListItem))
        {
            return new InvoiceListItem(
                new SharedContracts.InvoiceDto
                {
                    Id = FixtureId,
                    CustomerId = FixtureId,
                    CustomerName = "대표 데이터",
                    InvoiceNumber = "UI-MATRIX",
                    InvoiceDate = DateOnly.FromDateTime(DateTime.UtcNow),
                    TotalAmount = 1,
                    VoucherType = SharedContracts.VoucherType.Sales,
                    Lines = [],
                    Payments = []
                },
                "대표 데이터");
        }
        if (depth > 2)
            return DefaultValue(type, string.Empty, depth);
        var simple = DefaultValue(type, type.Name, depth);
        if (simple is not null || type.IsValueType)
            return simple;

        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(type, nonPublic: true);
        }
        catch
        {
            foreach (var constructor in type.GetConstructors(
                         BindingFlags.Instance | BindingFlags.Public |
                         BindingFlags.NonPublic).OrderBy(item => item.GetParameters().Length))
            {
                try
                {
                    var arguments = constructor.GetParameters()
                        .Select(parameter =>
                            DefaultValue(parameter.ParameterType, parameter.Name ?? string.Empty, depth + 1))
                        .ToArray();
                    instance = constructor.Invoke(arguments);
                    break;
                }
                catch
                {
                    // Try the next bounded constructor shape.
                }
            }
        }

        if (instance is null)
            return null;
        foreach (var property in type.GetProperties(
                     BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanWrite || property.GetIndexParameters().Length != 0)
                continue;
            try
            {
                property.SetValue(
                    instance,
                    DefaultValue(property.PropertyType, property.Name, depth + 1));
            }
            catch
            {
                // A fixture only needs enough readable values to realize its template.
            }
        }
        return instance;
    }

    private static object? DefaultValue(Type type, string name, int depth)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (nullable is not null)
            return DefaultValue(nullable, name, depth);
        if (type == typeof(string))
        {
            if (name.Contains("FileName", StringComparison.OrdinalIgnoreCase))
                return "ui-matrix-evidence.pdf";
            if (name.Contains("ContentType", StringComparison.OrdinalIgnoreCase))
                return "application/pdf";
            if (name.Contains("Code", StringComparison.OrdinalIgnoreCase))
                return "UI-MATRIX";
            return "대표 데이터";
        }
        if (type == typeof(Guid))
            return FixtureId;
        if (type == typeof(DateTime))
            return DateTime.UtcNow;
        if (type == typeof(DateTimeOffset))
            return DateTimeOffset.UtcNow;
        if (type == typeof(bool))
            return name.StartsWith("Is", StringComparison.OrdinalIgnoreCase) &&
                   !name.Contains("Deleted", StringComparison.OrdinalIgnoreCase);
        if (type == typeof(byte[]))
            return new byte[] { 1 };
        if (type.IsEnum)
        {
            var values = Enum.GetValues(type);
            return values.Length == 0 ? Activator.CreateInstance(type) : values.GetValue(0);
        }
        if (type.IsPrimitive || type == typeof(decimal))
            return Convert.ChangeType(1, type, System.Globalization.CultureInfo.InvariantCulture);
        if (type.IsValueType)
            return Activator.CreateInstance(type);
        if (type.IsGenericType &&
            type.GetGenericTypeDefinition() is var definition &&
            (definition == typeof(List<>) || definition == typeof(IList<>) ||
             definition == typeof(IReadOnlyList<>) || definition == typeof(IEnumerable<>)))
        {
            return Activator.CreateInstance(typeof(List<>).MakeGenericType(type.GetGenericArguments()[0]));
        }
        return depth <= 2 ? CreateFixture(type, depth + 1) : null;
    }

    private static async Task WaitForLayoutAsync(View root)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            if (root.Width > 0 && root.Height > 0)
                return;
            await Task.Delay(50);
        }
    }

    private static async Task WaitForActionLayoutAsync(
        IReadOnlyList<ResolvedAction> actions)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(50);
            if (actions.All(action =>
                    action.Element.Width > 0 && action.Element.Height > 0))
                return;
        }
    }

    private static IReadOnlyList<ResolvedAction> ResolveActions(
        View root,
        MobileUiMatrixRequest request,
        IReadOnlyDictionary<VisualElement, bool> naturalVisibility)
    {
        var resolved = new List<ResolvedAction>();
        var usedButtons = new HashSet<Button>();
        var registered = MobileUiMatrixActionRegistry.Snapshot(request.Page)
            .Where(entry => IsDescendantOf(entry.Button, root))
            .ToArray();
        var visualButtons = EnumerateVisualElements(root)
            .OfType<Button>()
            .ToArray();

        foreach (var expected in request.Actions.Where(action =>
                     string.Equals(action.Kind, "Button", StringComparison.Ordinal)))
        {
            var expectedLine = ReadStableLine(expected.StableId);
            var candidates = registered
                .Where(entry => !usedButtons.Contains(entry.Button))
                .Where(entry => string.IsNullOrWhiteSpace(expected.Label) ||
                                LabelsMatch(entry.Text, expected.Label))
                .OrderBy(entry => Math.Abs(entry.SourceLine - expectedLine))
                .ToArray();
            Button? selected = candidates.FirstOrDefault()?.Button;
            if (selected is null)
            {
                var visibleCandidates = visualButtons
                    .Where(button => !usedButtons.Contains(button))
                    .Where(button => string.IsNullOrWhiteSpace(expected.Label) ||
                                     LabelsMatch(button.Text, expected.Label))
                    .ToArray();
                if (string.IsNullOrWhiteSpace(expected.Label) &&
                    expected.VisualOrdinal > 0 &&
                    expected.VisualOrdinal <= visualButtons.Length)
                {
                    var ordinalCandidate = visualButtons[expected.VisualOrdinal - 1];
                    if (!usedButtons.Contains(ordinalCandidate))
                        selected = ordinalCandidate;
                }
                selected ??= visibleCandidates.FirstOrDefault();
            }
            if (selected is null)
                continue;
            usedButtons.Add(selected);
            selected.AutomationId = "GEORAEPLAN_UI_MATRIX_" + expected.StableId;
            var naturalState = naturalVisibility.TryGetValue(selected, out var visible) && visible;
            ForceVisible(selected);
            resolved.Add(new ResolvedAction(expected, selected, naturalState));
        }

        foreach (var button in visualButtons.Where(button => !usedButtons.Contains(button)))
            button.IsVisible = false;

        var gestures = EnumerateVisualElements(root)
            .OfType<View>()
            .SelectMany(view => view.GestureRecognizers
                .OfType<TapGestureRecognizer>()
                .Select(gesture => (View: view, Gesture: gesture)))
            .ToList();
        foreach (var expected in request.Actions.Where(action =>
                     string.Equals(action.Kind, "TapGesture", StringComparison.Ordinal)))
        {
            var gestureIndex = expected.VisualOrdinal - 1;
            if (gestureIndex < 0 || gestureIndex >= gestures.Count)
                continue;
            var owner = gestures[gestureIndex].View;
            owner.AutomationId = "GEORAEPLAN_UI_MATRIX_" + expected.StableId;
            var naturalState = naturalVisibility.TryGetValue(owner, out var visible) && visible;
            ForceVisible(owner);
            resolved.Add(new ResolvedAction(expected, owner, naturalState));
        }

        return resolved;
    }

    private async Task<MobileUiMatrixActionResult> MeasureActionAsync(
        ResolvedAction action,
        Rect initialBounds)
    {
        var element = action.Element;
        var bounds = initialBounds;
        var onScreen = IsInsideViewport(bounds);
        var reachable = IsReachableInViewport(bounds);
        if (!reachable && element is View view)
        {
            try
            {
                foreach (var itemsView in FindAncestors<ItemsView>(element))
                {
                    var item = FindItemBindingContext(itemsView, element);
                    if (item is not null)
                    {
                        itemsView.ScrollTo(
                            item,
                            position: ScrollToPosition.MakeVisible,
                            animate: false);
                        await Task.Delay(50);
                    }
                }
                foreach (var scroll in FindAncestors<ScrollView>(element))
                {
                    await scroll.ScrollToAsync(view, ScrollToPosition.MakeVisible, false);
                    await Task.Delay(50);
                }
                if (element.Handler?.PlatformView is Android.Views.View native)
                {
                    native.RequestRectangleOnScreen(
                        new Android.Graphics.Rect(0, 0, native.Width, native.Height),
                        true);
                    await Task.Delay(100);
                }
                bounds = GetViewportBounds(element);
                reachable = IsReachableInViewport(bounds);
            }
            catch
            {
                reachable = false;
            }
        }

        var textFits = true;
        if (element is Button button && element.Width > 0 && element.Height > 0)
            textFits = DoesRenderedTextFit(button);

        return new MobileUiMatrixActionResult
        {
            StableId = action.Expected.StableId,
            Kind = action.Expected.Kind,
            Text = element is Button measuredButton ? measuredButton.Text ?? string.Empty : string.Empty,
            X = bounds.X,
            Y = bounds.Y,
            Width = bounds.Width,
            Height = bounds.Height,
            Visible = IsEffectivelyVisible(element),
            Enabled = element.IsEnabled,
            OnScreen = onScreen,
            Reachable = reachable,
            TextFits = textFits,
            NaturalState = action.NaturalState,
            InitialX = initialBounds.X,
            InitialY = initialBounds.Y,
            InitialWidth = initialBounds.Width,
            InitialHeight = initialBounds.Height
        };
    }

    private static IReadOnlyList<MeasuredTextElement> MeasureVisibleTextElements(
        View root)
    {
        var results = new List<MeasuredTextElement>();
        var ordinal = 0;
        foreach (var element in EnumerateVisualElements(root))
        {
            string text;
            bool wraps;
            switch (element)
            {
                case Label label when !string.IsNullOrWhiteSpace(label.Text):
                    text = label.Text;
                    wraps = label.LineBreakMode == LineBreakMode.WordWrap;
                    break;
                case Button button when !string.IsNullOrWhiteSpace(button.Text):
                    text = button.Text;
                    wraps = button.LineBreakMode == LineBreakMode.WordWrap;
                    break;
                default:
                    continue;
            }

            if (!IsEffectivelyVisible(element))
                continue;
            if (element.Handler?.PlatformView is not Android.Views.View native ||
                native.Width <= 0 || native.Height <= 0)
                continue;

            ordinal++;
            var measurement = MeasureRenderedText(element);

            results.Add(new MeasuredTextElement(
                $"{element.GetType().Name}-{ordinal}",
                text.Length,
                wraps,
                measurement.Fits,
                measurement.Diagnostic));
        }
        return results;
    }

    private static async Task RealizeItemTemplatesAsync(View root)
    {
        var itemViews = EnumerateVisualElements(root).OfType<ItemsView>().ToArray();
        foreach (var itemsView in itemViews)
        {
            var firstItem = (itemsView.ItemsSource as IEnumerable)?
                .Cast<object?>()
                .FirstOrDefault(item => item is not null);
            if (firstItem is null)
                continue;

            foreach (var scroll in FindAncestors<ScrollView>(itemsView))
            {
                await scroll.ScrollToAsync(itemsView, ScrollToPosition.MakeVisible, false);
                await Task.Delay(40);
            }
            itemsView.ScrollTo(
                firstItem,
                position: ScrollToPosition.MakeVisible,
                animate: false);
            await Task.Delay(80);
        }
    }

    private static bool DoesRenderedTextFit(VisualElement element)
        => MeasureRenderedText(element).Fits;

    private static RenderedTextMeasurement MeasureRenderedText(VisualElement element)
    {
        if (element.Width <= 0 || element.Height <= 0)
            return new RenderedTextMeasurement(
                false,
                $"view={element.Width:F1}x{element.Height:F1}:empty-bounds");

        if (element.Handler?.PlatformView is Android.Widget.TextView native &&
            native.Layout is { } layout)
        {
            var ellipsis = 0;
            for (var line = 0; line < layout.LineCount; line++)
                ellipsis += layout.GetEllipsisCount(line);

            var availableHeight = native.Height -
                                  native.CompoundPaddingTop -
                                  native.CompoundPaddingBottom;
            var fits = availableHeight > 0 &&
                       ellipsis == 0 &&
                       layout.Height <= availableHeight + 2;
            return new RenderedTextMeasurement(
                fits,
                $"view={element.Width:F1}x{element.Height:F1}:native={native.Width}x{native.Height}:available={availableHeight}:layout={layout.Height}:lines={layout.LineCount}:ellipsis={ellipsis}");
        }

        var desired = ((IView)element).Measure(
            Math.Max(1, element.Width),
            double.PositiveInfinity);
        return new RenderedTextMeasurement(
            desired.Height <= element.Height + 2,
            $"view={element.Width:F1}x{element.Height:F1}:desired={desired.Width:F1}x{desired.Height:F1}");
    }

    private static IEnumerable<VisualElement> EnumerateVisualElements(
        IVisualTreeElement root)
    {
        if (root is VisualElement element)
            yield return element;
        foreach (var child in root.GetVisualChildren())
        {
            foreach (var descendant in EnumerateVisualElements(child))
                yield return descendant;
        }
    }

    private static bool IsDescendantOf(Element element, Element root)
    {
        for (Element? current = element; current is not null; current = current.Parent)
        {
            if (ReferenceEquals(current, root))
                return true;
        }
        return false;
    }

    private static void ForceVisible(VisualElement element)
    {
        for (Element? current = element; current is VisualElement visual; current = current.Parent)
        {
            visual.RemoveBinding(VisualElement.IsVisibleProperty);
            visual.IsVisible = true;
        }
    }

    private static bool IsEffectivelyVisible(VisualElement element)
    {
        for (Element? current = element; current is VisualElement visual; current = current.Parent)
        {
            if (!visual.IsVisible)
                return false;
        }
        return true;
    }

    private static IReadOnlyList<T> FindAncestors<T>(Element element) where T : Element
    {
        var matches = new List<T>();
        for (Element? current = element.Parent; current is not null; current = current.Parent)
        {
            if (current is T match)
                matches.Add(match);
        }
        return matches;
    }

    private static object? FindItemBindingContext(
        ItemsView itemsView,
        Element element)
    {
        Element current = element;
        while (current.Parent is Element parent &&
               !ReferenceEquals(parent, itemsView))
        {
            current = parent;
        }
        return current.BindingContext;
    }

    private Rect GetViewportBounds(VisualElement element)
    {
        if (element.Handler?.PlatformView is Android.Views.View native &&
            Handler?.PlatformView is Android.Views.View hostNative)
        {
            var elementLocation = new int[2];
            var hostLocation = new int[2];
            native.GetLocationOnScreen(elementLocation);
            hostNative.GetLocationOnScreen(hostLocation);
            var density = native.Resources?.DisplayMetrics?.Density ?? 1f;
            if (density > 0)
            {
                return new Rect(
                    (elementLocation[0] - hostLocation[0]) / density,
                    (elementLocation[1] - hostLocation[1]) / density,
                    native.Width / density,
                    native.Height / density);
            }
        }

        var x = element.X + element.TranslationX;
        var y = element.Y + element.TranslationY;
        for (Element? current = element.Parent; current is VisualElement visual; current = current.Parent)
        {
            x += visual.X + visual.TranslationX;
            y += visual.Y + visual.TranslationY;
        }
        return new Rect(x, y, element.Width, element.Height);
    }

    private bool IsInsideViewport(Rect bounds)
    {
        var viewport = GetSafeViewport();
        return bounds.Width > 0 && bounds.Height > 0 &&
               bounds.Left >= viewport.Left - 1 &&
               bounds.Top >= viewport.Top - 1 &&
               bounds.Right <= viewport.Right + 1 &&
               bounds.Bottom <= viewport.Bottom + 1;
    }

    private bool IsReachableInViewport(Rect bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return false;
        var viewport = GetSafeViewport();
        var visibleWidth = Math.Min(bounds.Right, viewport.Right) -
                           Math.Max(bounds.Left, viewport.Left);
        var visibleHeight = Math.Min(bounds.Bottom, viewport.Bottom) -
                            Math.Max(bounds.Top, viewport.Top);
        return visibleWidth >= Math.Min(24, bounds.Width) &&
               visibleHeight >= Math.Min(24, bounds.Height);
    }

    private Rect GetSafeViewport()
    {
        var viewport = new Rect(0, 0, Width, Height);
        if (Handler?.PlatformView is not Android.Views.View hostNative)
            return viewport;

        var visibleFrame = new Android.Graphics.Rect();
        hostNative.GetWindowVisibleDisplayFrame(visibleFrame);
        var hostLocation = new int[2];
        hostNative.GetLocationOnScreen(hostLocation);
        var density = hostNative.Resources?.DisplayMetrics?.Density ?? 1f;
        if (density <= 0)
            return viewport;

        var left = Math.Max(0, (visibleFrame.Left - hostLocation[0]) / density);
        var top = Math.Max(0, (visibleFrame.Top - hostLocation[1]) / density);
        var right = Math.Min(
            Width,
            (visibleFrame.Right - hostLocation[0]) / density);
        var bottom = Math.Min(
            Height,
            (visibleFrame.Bottom - hostLocation[1]) / density);
        if (right <= left || bottom <= top)
            return viewport;

        return new Rect(left, top, right - left, bottom - top);
    }

    private static bool IntersectsInitial(
        MobileUiMatrixActionResult left,
        MobileUiMatrixActionResult right)
        => Math.Min(left.InitialX + left.InitialWidth, right.InitialX + right.InitialWidth) -
               Math.Max(left.InitialX, right.InitialX) > 1 &&
           Math.Min(left.InitialY + left.InitialHeight, right.InitialY + right.InitialHeight) -
               Math.Max(left.InitialY, right.InitialY) > 1;

    private static int ReadStableLine(string stableId)
    {
        var marker = stableId.IndexOf("#L", StringComparison.Ordinal);
        if (marker < 0 || stableId.Length < marker + 7)
            return int.MaxValue / 2;
        return int.TryParse(stableId.AsSpan(marker + 2, 4), out var line)
            ? line
            : int.MaxValue / 2;
    }

    private static bool LabelsMatch(string actual, string expected)
        => string.Equals(actual, expected, StringComparison.Ordinal) ||
           (string.Equals(actual, "접기", StringComparison.Ordinal) &&
            string.Equals(expected, "닫기", StringComparison.Ordinal));

    private static MobileUiMatrixRequest DecodeRequest(string encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
            throw new InvalidOperationException("UI matrix request is empty.");
        var bytes = Convert.FromBase64String(encoded);
        try
        {
            if (bytes.Length == 0 || bytes.Length > MaximumRequestBytes)
                throw new InvalidOperationException("UI matrix request size is invalid.");
            return JsonSerializer.Deserialize<MobileUiMatrixRequest>(bytes) ??
                   throw new InvalidOperationException("UI matrix request JSON is empty.");
        }
        finally
        {
            Array.Clear(bytes, 0, bytes.Length);
        }
    }

    private static string TryReadMeasurementId(string encoded)
    {
        try
        {
            return DecodeRequest(encoded).MeasurementId;
        }
        catch
        {
            return "INVALID";
        }
    }

    private static void ValidateRequest(MobileUiMatrixRequest request)
    {
        if (request.SchemaVersion != 1 ||
            string.IsNullOrWhiteSpace(request.MeasurementId) ||
            string.IsNullOrWhiteSpace(request.Page) ||
            string.IsNullOrWhiteSpace(request.StateRequirement) ||
            string.IsNullOrWhiteSpace(request.StateOwner) ||
            string.IsNullOrWhiteSpace(request.Scenario) ||
            request.Actions.Count == 0 ||
            request.Actions.Select(action => action.StableId).Distinct(StringComparer.Ordinal).Count() !=
            request.Actions.Count ||
            request.Actions.Any(action =>
                action.Kind is not ("Button" or "TapGesture") ||
                action.VisualOrdinal <= 0))
        {
            throw new InvalidOperationException("UI matrix request contract is invalid.");
        }
        if (request.Keyboard is not ("closed" or "open"))
            throw new InvalidOperationException("UI matrix keyboard contract is invalid.");
    }

    private static void PublishResult(MobileUiMatrixResult result)
    {
        var json = JsonSerializer.Serialize(result);
        var root = FileSystem.CacheDirectory;
        var finalPath = System.IO.Path.Combine(root, ResultFileName);
        var temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           BufferSize = 4096,
                           Options = FileOptions.WriteThrough
                       }))
            {
                var bytes = Encoding.UTF8.GetBytes(json);
                try
                {
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush(true);
                }
                finally
                {
                    Array.Clear(bytes, 0, bytes.Length);
                }
            }
            File.Move(temporaryPath, finalPath, true);
            Log.Info(
                LogTag,
                $"GEORAEPLAN_UI_MATRIX_READY_V1 {result.MeasurementId} {ResultFileName}");
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed record ResolvedAction(
        MobileUiMatrixExpectedAction Expected,
        VisualElement Element,
        bool NaturalState);

    private sealed record MeasuredTextElement(
        string StableId,
        int TextLength,
        bool Wraps,
        bool TextFits,
        string Diagnostic);

    private sealed record RenderedTextMeasurement(
        bool Fits,
        string Diagnostic);
}

internal sealed class MobileUiMatrixRequest
{
    public int SchemaVersion { get; init; }
    public string MeasurementId { get; init; } = string.Empty;
    public string Page { get; init; } = string.Empty;
    public string StateRequirement { get; init; } = string.Empty;
    public string StateOwner { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Keyboard { get; init; } = string.Empty;
    public List<MobileUiMatrixExpectedAction> Actions { get; init; } = [];
}

internal sealed class MobileUiMatrixExpectedAction
{
    public string StableId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public int VisualOrdinal { get; init; }
}

internal sealed class MobileUiMatrixResult
{
    public int SchemaVersion { get; init; } = 1;
    public string MeasurementId { get; init; } = string.Empty;
    public string Page { get; init; } = string.Empty;
    public string StateRequirement { get; init; } = string.Empty;
    public string Scenario { get; init; } = string.Empty;
    public string Keyboard { get; init; } = string.Empty;
    public int ExpectedActionCount { get; init; }
    public int ActualActionCount { get; init; }
    public int TextElementCount { get; init; }
    public bool KeyboardFocused { get; init; }
    public double ViewportWidth { get; init; }
    public double ViewportHeight { get; init; }
    public bool Passed { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public IReadOnlyList<MobileUiMatrixActionResult> Actions { get; init; } = [];
}

internal sealed class MobileUiMatrixActionResult
{
    public string StableId { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; }
    public double Height { get; init; }
    public bool Visible { get; init; }
    public bool Enabled { get; init; }
    public bool OnScreen { get; init; }
    public bool Reachable { get; init; }
    public bool TextFits { get; init; }
    public bool NaturalState { get; init; }
    public double InitialX { get; init; }
    public double InitialY { get; init; }
    public double InitialWidth { get; init; }
    public double InitialHeight { get; init; }
}
#endif
