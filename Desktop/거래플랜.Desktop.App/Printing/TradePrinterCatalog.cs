using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace 거래플랜.Desktop.App.Printing;

public sealed record PrinterCatalogItem(
    string QueueName,
    string DisplayName,
    string TypeText,
    string LocationText,
    string StatusText,
    bool IsOffline,
    bool IsDefault);

public sealed record PrinterCatalogSnapshot(
    IReadOnlyList<PrinterCatalogItem> Printers,
    string? DefaultQueueName);

public static class TradePrinterCatalog
{
    private const uint PrinterEnumLocal = 0x00000002;
    private const uint PrinterEnumConnections = 0x00000004;
    private const uint PrinterInfoLevel = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int MaxEnumerationAttempts = 3;
    private const uint StatusPaused = 0x00000001;
    private const uint StatusError = 0x00000002;
    private const uint StatusPaperJam = 0x00000008;
    private const uint StatusPaperOut = 0x00000010;
    private const uint StatusOffline = 0x00000080;
    private const uint StatusBusy = 0x00000200;
    private const uint StatusPrinting = 0x00000400;
    private const uint StatusNotAvailable = 0x00001000;
    private const uint StatusTonerLow = 0x00020000;
    private const uint StatusNoToner = 0x00040000;
    private const uint StatusUserIntervention = 0x00100000;
    private const uint StatusDoorOpen = 0x00400000;
    private const uint StatusServerUnknown = 0x00800000;

    public static PrinterCatalogSnapshot LoadSnapshot()
    {
        var defaultName = TryGetDefaultPrinterName();
        var items = EnumeratePrinterInfo()
            .Where(static info => !string.IsNullOrWhiteSpace(info.QueueName))
            .Select(info => CreateCatalogItem(info, defaultName))
            .GroupBy(static item => item.QueueName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderByDescending(static item => item.IsDefault)
            .ThenBy(static item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new PrinterCatalogSnapshot(
            new ReadOnlyCollection<PrinterCatalogItem>(items),
            defaultName);
    }

    public static IReadOnlyList<string> LoadWindowsInstalledPrinterNames()
        => EnumeratePrinterInfo()
            .Select(static info => info.QueueName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static PrinterCatalogItem CreateCatalogItem(PrinterInfoSnapshot info, string? defaultName)
    {
        var isDefault = NamesEqual(info.QueueName, defaultName);
        var typeParts = new[] { info.DriverName, info.ShareName }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var typeText = typeParts.Length == 0
            ? info.QueueName
            : string.Join(" / ", typeParts);
        var locationText = !string.IsNullOrWhiteSpace(info.Location)
            ? info.Location
            : !string.IsNullOrWhiteSpace(info.Comment)
                ? info.Comment
                : "-";

        return new PrinterCatalogItem(
            info.QueueName,
            isDefault ? $"{info.QueueName} (기본)" : info.QueueName,
            typeText,
            locationText,
            FormatStatus(info.Status),
            HasAnyStatus(info.Status, StatusOffline | StatusNotAvailable | StatusServerUnknown),
            isDefault);
    }

    private static IReadOnlyList<PrinterInfoSnapshot> EnumeratePrinterInfo()
    {
        IntPtr buffer = IntPtr.Zero;
        uint bufferSize = 0;
        try
        {
            var flags = PrinterEnumLocal | PrinterEnumConnections;
            for (var attempt = 1; attempt <= MaxEnumerationAttempts; attempt++)
            {
                if (bufferSize == 0)
                {
                    var probeSucceeded = EnumPrinters(
                        flags,
                        null,
                        PrinterInfoLevel,
                        IntPtr.Zero,
                        0,
                        out var requiredBytes,
                        out _);
                    var probeError = Marshal.GetLastWin32Error();
                    if (requiredBytes == 0)
                    {
                        if (probeSucceeded)
                            return Array.Empty<PrinterInfoSnapshot>();
                        throw new Win32Exception(probeError);
                    }
                    if (!probeSucceeded && probeError != ErrorInsufficientBuffer)
                        throw new Win32Exception(probeError);
                    bufferSize = requiredBytes;
                }

                if (buffer != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(buffer);
                    buffer = IntPtr.Zero;
                }
                buffer = Marshal.AllocHGlobal(checked((int)bufferSize));
                if (EnumPrinters(
                        flags,
                        null,
                        PrinterInfoLevel,
                        buffer,
                        bufferSize,
                        out var requiredBytesAfterRead,
                        out var returnedCount))
                {
                    return ReadPrinterInfo(buffer, returnedCount);
                }

                var readError = Marshal.GetLastWin32Error();
                if (readError != ErrorInsufficientBuffer ||
                    requiredBytesAfterRead == 0 ||
                    attempt == MaxEnumerationAttempts)
                {
                    throw new Win32Exception(readError);
                }
                bufferSize = Math.Max(bufferSize, requiredBytesAfterRead);
            }

            throw new Win32Exception(ErrorInsufficientBuffer);
        }
        finally
        {
            if (buffer != IntPtr.Zero)
                Marshal.FreeHGlobal(buffer);
        }
    }

    private static IReadOnlyList<PrinterInfoSnapshot> ReadPrinterInfo(
        IntPtr buffer,
        uint returnedCount)
    {
        var entrySize = Marshal.SizeOf<PrinterInfo2>();
        var entries = new List<PrinterInfoSnapshot>(checked((int)returnedCount));
        for (var index = 0; index < returnedCount; index++)
        {
            var entry = Marshal.PtrToStructure<PrinterInfo2>(
                IntPtr.Add(buffer, checked((int)index * entrySize)));
            entries.Add(new PrinterInfoSnapshot(
                ReadText(entry.PrinterName),
                ReadText(entry.ShareName),
                ReadText(entry.DriverName),
                ReadText(entry.Location),
                ReadText(entry.Comment),
                entry.Status));
        }

        return entries;
    }

    private static string FormatStatus(uint status)
    {
        if (status == 0)
            return "준비";

        var values = new List<string>();
        Add(StatusOffline, "오프라인");
        Add(StatusError, "오류");
        Add(StatusPaperJam, "용지 걸림");
        Add(StatusPaperOut, "용지 없음");
        Add(StatusDoorOpen, "덮개 열림");
        Add(StatusNoToner, "토너 없음");
        Add(StatusTonerLow, "토너 부족");
        Add(StatusUserIntervention, "사용자 확인 필요");
        Add(StatusNotAvailable, "사용 불가");
        Add(StatusServerUnknown, "서버 상태 불명");
        Add(StatusPaused, "일시 중지");
        Add(StatusPrinting, "인쇄 중");
        Add(StatusBusy, "사용 중");
        return values.Count == 0 ? $"상태 0x{status:X8}" : string.Join(", ", values);

        void Add(uint flag, string label)
        {
            if (HasAnyStatus(status, flag))
                values.Add(label);
        }
    }

    private static bool HasAnyStatus(uint value, uint flags)
        => (value & flags) != 0;

    private static string ReadText(IntPtr value)
        => Marshal.PtrToStringUni(value)?.Trim() ?? string.Empty;

    private sealed record PrinterInfoSnapshot(
        string QueueName,
        string ShareName,
        string DriverName,
        string Location,
        string Comment,
        uint Status);

    private static string? TryGetDefaultPrinterName()
    {
        var characterCount = 0;
        _ = GetDefaultPrinter(null, ref characterCount);
        if (characterCount <= 1)
            return null;

        var buffer = new char[characterCount];
        if (!GetDefaultPrinter(buffer, ref characterCount))
            return null;
        var value = new string(buffer, 0, Math.Max(0, characterCount - 1)).TrimEnd('\0');
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static bool NamesEqual(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left) &&
           !string.IsNullOrWhiteSpace(right) &&
           string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    [DllImport(
        "winspool.drv",
        EntryPoint = "EnumPrintersW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPrinters(
        uint flags,
        string? name,
        uint level,
        IntPtr printerInfo,
        uint bufferSize,
        out uint requiredBytes,
        out uint returnedCount);

    [DllImport(
        "winspool.drv",
        EntryPoint = "GetDefaultPrinterW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDefaultPrinter(
        [Out] char[]? buffer,
        ref int bufferSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PrinterInfo2
    {
        public readonly IntPtr ServerName;
        public readonly IntPtr PrinterName;
        public readonly IntPtr ShareName;
        public readonly IntPtr PortName;
        public readonly IntPtr DriverName;
        public readonly IntPtr Comment;
        public readonly IntPtr Location;
        public readonly IntPtr DevMode;
        public readonly IntPtr SeparatorFile;
        public readonly IntPtr PrintProcessor;
        public readonly IntPtr DataType;
        public readonly IntPtr Parameters;
        public readonly IntPtr SecurityDescriptor;
        public readonly uint Attributes;
        public readonly uint Priority;
        public readonly uint DefaultPriority;
        public readonly uint StartTime;
        public readonly uint UntilTime;
        public readonly uint Status;
        public readonly uint JobCount;
        public readonly uint AveragePagesPerMinute;
    }
}
