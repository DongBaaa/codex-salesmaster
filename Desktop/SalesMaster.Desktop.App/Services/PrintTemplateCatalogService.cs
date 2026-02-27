using System.IO;

namespace SalesMaster.Desktop.App.Services;

public sealed class PrintTemplateCatalogService
{
    public const string DefaultStatementTemplateSettingKey = "Print.DefaultStatementTemplate";

    public const string BuiltInTradeHalfTemplateId = "__builtin_statement_trade_half";
    public const string BuiltInTradeA4TemplateId = "__builtin_statement_trade_a4";
    public const string BuiltInReceiptTemplateId = "__builtin_statement_receipt";

    public static readonly PrintTemplateOption BuiltInTradeHalfTemplate = new(
        BuiltInTradeHalfTemplateId,
        "내장 양식 - 거래명1/2",
        string.Empty,
        IsBuiltIn: true,
        BuiltInLayout: NativeStatementLayoutType.TradeHalf);

    public static readonly PrintTemplateOption BuiltInTradeA4Template = new(
        BuiltInTradeA4TemplateId,
        "내장 양식 - 거래명A4",
        string.Empty,
        IsBuiltIn: true,
        BuiltInLayout: NativeStatementLayoutType.TradeA4);

    public static readonly PrintTemplateOption BuiltInReceiptTemplate = new(
        BuiltInReceiptTemplateId,
        "내장 양식 - 영수증",
        string.Empty,
        IsBuiltIn: true,
        BuiltInLayout: NativeStatementLayoutType.Receipt);

    public static IReadOnlyList<PrintTemplateOption> BuiltInTemplates { get; } =
    [
        BuiltInTradeHalfTemplate,
        BuiltInTradeA4Template,
        BuiltInReceiptTemplate
    ];

    public IReadOnlyList<PrintTemplateOption> GetStatementTemplates()
    {
        var templates = new List<PrintTemplateOption>();
        var templateDirectory = ResolveTemplateDirectory();
        if (!string.IsNullOrWhiteSpace(templateDirectory))
        {
            var frxFiles = Directory.EnumerateFiles(templateDirectory, "*.frx", SearchOption.TopDirectoryOnly)
                .Where(IsValidFrxTemplate)
                .ToList();
            var frxBaseNames = new HashSet<string>(
                frxFiles.Select(Path.GetFileNameWithoutExtension)
                    .Where(static name => !string.IsNullOrWhiteSpace(name))
                    .Select(static name => name!),
                StringComparer.CurrentCultureIgnoreCase);

            var fr3Files = Directory.EnumerateFiles(templateDirectory, "*.fr3", SearchOption.TopDirectoryOnly)
                .Where(file => !frxBaseNames.Contains(Path.GetFileNameWithoutExtension(file)))
                .ToList();

            var files = frxFiles
                .Concat(fr3Files)
                .OrderBy(path => GetLegacyTemplateSortOrder(Path.GetFileNameWithoutExtension(path) ?? string.Empty))
                .ThenBy(Path.GetFileNameWithoutExtension, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(path => Path.GetExtension(path).Equals(".frx", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ToList();

            foreach (var filePath in files)
            {
                var fileName = Path.GetFileNameWithoutExtension(filePath);
                var ext = Path.GetExtension(filePath).ToUpperInvariant();
                templates.Add(new PrintTemplateOption(
                    Id: filePath,
                    DisplayName: $"{fileName} ({ext.TrimStart('.')})",
                    TemplatePath: filePath,
                    IsBuiltIn: false));
            }
        }

        // Legacy mechanism prefers external FR templates; built-in is fallback.
        templates.AddRange(BuiltInTemplates);
        return templates;
    }

    public string? ResolveTemplateDirectory()
    {
        var candidates = BuildTemplateDirectoryCandidates();
        return candidates.FirstOrDefault(Contains외부 리포팅 도구Template);
    }

    public static PrintTemplateOption? ResolvePreferredTemplate(
        IReadOnlyList<PrintTemplateOption> templates,
        string? savedTemplateId,
        string? printType)
    {
        if (templates.Count == 0)
            return null;

        if (!string.IsNullOrWhiteSpace(savedTemplateId))
        {
            var saved = templates.FirstOrDefault(t => string.Equals(t.Id, savedTemplateId, StringComparison.OrdinalIgnoreCase));
            if (saved is not null)
                return saved;
        }

        var legacyDefault = ResolveLegacyTemplateForPrintType(templates, printType);
        if (legacyDefault is not null)
            return legacyDefault;

        return templates.FirstOrDefault(t => t.IsBuiltIn && t.BuiltInLayout == NativeStatementLayoutType.TradeHalf)
            ?? templates.FirstOrDefault(t => t.IsBuiltIn)
            ?? templates.FirstOrDefault();
    }

    public static PrintTemplateOption? ResolveLegacyTemplateForPrintType(
        IReadOnlyList<PrintTemplateOption> templates,
        string? printType)
    {
        if (templates.Count == 0)
            return null;

        var external = templates.Where(t => !t.IsBuiltIn && !string.IsNullOrWhiteSpace(t.TemplatePath)).ToList();
        if (external.Count == 0)
            return null;

        var targetGroup = ResolveLegacyTemplateGroup(printType);
        var candidates = targetGroup switch
        {
            LegacyTemplateGroup.Receipt => LegacyReceiptTemplateNames,
            LegacyTemplateGroup.TradeA4 => LegacyTradeA4TemplateNames,
            _ => LegacyTradeHalfTemplateNames
        };

        foreach (var name in candidates)
        {
            var match = external.FirstOrDefault(t => MatchesTemplateName(t, name));
            if (match is not null)
                return match;
        }

        return external.FirstOrDefault();
    }

    public static NativeStatementLayoutType ResolveBuiltInLayoutFromPrintType(string? printType)
    {
        if (string.IsNullOrWhiteSpace(printType))
            return NativeStatementLayoutType.TradeHalf;

        if (printType.Contains("영수", StringComparison.OrdinalIgnoreCase))
            return NativeStatementLayoutType.Receipt;
        if (printType.Contains("A4", StringComparison.OrdinalIgnoreCase))
            return NativeStatementLayoutType.TradeA4;

        return NativeStatementLayoutType.TradeHalf;
    }

    private enum LegacyTemplateGroup
    {
        TradeHalf = 0,
        TradeA4 = 1,
        Receipt = 2
    }

    private static readonly string[] LegacyTradeHalfTemplateNames =
    [
        "박사_명세21_2",
        "P_거래명세21_2",
        "P_거래명세21_1",
        "박사_거래21_2",
        "박사_거래21_1",
        "거래명세21_2",
        "거래명세21_1"
    ];

    private static readonly string[] LegacyTradeA4TemplateNames =
    [
        "박사_명세A4_2",
        "P_거래명세A4_2",
        "P_거래명세A4_1",
        "거래명세A4_2",
        "거래명세A4_1"
    ];

    private static readonly string[] LegacyReceiptTemplateNames =
    [
        "박사_영수21_2",
        "P_거래영수21_2",
        "P_거래영수21_1",
        "거래영수21_2",
        "거래영수21_1"
    ];

    private static LegacyTemplateGroup ResolveLegacyTemplateGroup(string? printType)
    {
        if (string.IsNullOrWhiteSpace(printType))
            return LegacyTemplateGroup.TradeHalf;

        if (printType.Contains("영수", StringComparison.OrdinalIgnoreCase))
            return LegacyTemplateGroup.Receipt;

        if (printType.Contains("A4", StringComparison.OrdinalIgnoreCase))
            return LegacyTemplateGroup.TradeA4;

        return LegacyTemplateGroup.TradeHalf;
    }

    private static bool MatchesTemplateName(PrintTemplateOption option, string expectedName)
    {
        if (string.IsNullOrWhiteSpace(expectedName))
            return false;

        var fileName = Path.GetFileNameWithoutExtension(option.TemplatePath);
        if (!string.IsNullOrWhiteSpace(fileName) &&
            string.Equals(fileName, expectedName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return option.DisplayName.Contains(expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetLegacyTemplateSortOrder(string fileNameWithoutExt)
    {
        if (string.IsNullOrWhiteSpace(fileNameWithoutExt))
            return int.MaxValue;

        var name = fileNameWithoutExt;
        if (LegacyTradeHalfTemplateNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return 0;
        if (LegacyTradeA4TemplateNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return 1;
        if (LegacyReceiptTemplateNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
            return 2;

        return 100;
    }

    private static IReadOnlyList<string> BuildTemplateDirectoryCandidates()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<string>();

        static void AddCandidate(HashSet<string> seen, List<string> list, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            var full = Path.GetFullPath(path);
            if (seen.Add(full))
                list.Add(full);
        }

        AddCandidate(set, candidates, Path.Combine(AppContext.BaseDirectory, "양식"));
        AddCandidate(set, candidates, Path.Combine(Environment.CurrentDirectory, "양식"));
        AddCandidate(set, candidates, Path.Combine(AppContext.BaseDirectory, "REPO_출력물"));
        AddCandidate(set, candidates, Path.Combine(Environment.CurrentDirectory, "REPO_출력물"));

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            AddCandidate(set, candidates, Path.Combine(dir.FullName, "양식"));
            AddCandidate(set, candidates, Path.Combine(dir.FullName, "REPO_출력물"));
            dir = dir.Parent;
        }

        return candidates;
    }

    private static bool Contains외부 리포팅 도구Template(string path)
    {
        if (!Directory.Exists(path))
            return false;

        return Directory.EnumerateFiles(path, "*.fr3", SearchOption.TopDirectoryOnly).Any()
            || Directory.EnumerateFiles(path, "*.frx", SearchOption.TopDirectoryOnly).Any();
    }

    private static bool IsValidFrxTemplate(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < 512)
                return false;

            using var stream = File.OpenRead(path);
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            var head = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(head))
                return false;

            return head.Contains("<?xml", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
