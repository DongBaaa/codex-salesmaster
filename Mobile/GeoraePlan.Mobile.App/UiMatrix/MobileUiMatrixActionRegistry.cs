#if GEORAEPLAN_MOBILE_UI_MATRIX
namespace GeoraePlan.Mobile.App.UiMatrix;

internal sealed record MobileUiMatrixRegisteredButton(
    Button Button,
    string Page,
    string Text,
    int SourceLine);

internal static class MobileUiMatrixActionRegistry
{
    private static readonly object Sync = new();
    private static readonly List<MobileUiMatrixRegisteredButton> Buttons = [];

    public static void Reset()
    {
        lock (Sync)
            Buttons.Clear();
    }

    public static void RegisterButton(
        Button button,
        string sourceFile,
        int sourceLine)
    {
        ArgumentNullException.ThrowIfNull(button);
        var normalizedSourceFile = (sourceFile ?? string.Empty).Replace('\\', '/');
        var page = Path.GetFileNameWithoutExtension(normalizedSourceFile);
        lock (Sync)
        {
            Buttons.Add(new MobileUiMatrixRegisteredButton(
                button,
                page,
                button.Text ?? string.Empty,
                sourceLine));
        }
    }

    public static IReadOnlyList<MobileUiMatrixRegisteredButton> Snapshot(
        string page)
    {
        lock (Sync)
        {
            return Buttons
                .Where(entry => string.Equals(
                    entry.Page,
                    page,
                    StringComparison.Ordinal))
                .ToArray();
        }
    }

    public static IReadOnlyList<MobileUiMatrixRegisteredButton> SnapshotAll()
    {
        lock (Sync)
            return Buttons.ToArray();
    }
}
#endif
