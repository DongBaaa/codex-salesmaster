using Xunit;

namespace GeoraePlan.Server.Api.Tests;

public sealed class OfficeScopeFallbackGuardTests
{
    private static readonly string[] GuardedMethods =
    [
        "CanReadOfficeForCustomers",
        "CanWriteOfficeForCustomers",
        "CanReadOfficeForInvoices",
        "CanWriteOfficeForInvoices",
        "CanReadOfficeForPayments",
        "CanWriteOfficeForPayments",
        "CanReadOfficeForRentals",
        "CanWriteOfficeForRentals"
    ];

    [Fact]
    public void ServerDirectResponsibleOfficeScopeChecks_PassOwnerOfficeFallback()
    {
        var repositoryRoot = FindRepositoryRoot();
        var serverRoot = Path.Combine(repositoryRoot.FullName, "Server");
        var sourceFiles = Directory
            .EnumerateFiles(serverRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(
                Path.Combine("Services", "OfficeScopeService.cs"),
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var violations = new List<string>();
        foreach (var file in sourceFiles)
        {
            var source = File.ReadAllText(file);
            foreach (var call in FindMethodCalls(source))
            {
                if (!call.Arguments.Contains("ResponsibleOfficeCode", StringComparison.Ordinal))
                    continue;

                var argumentCount = SplitTopLevelArguments(call.Arguments).Count;
                if (argumentCount < 3)
                {
                    violations.Add(
                        $"{Path.GetRelativePath(repositoryRoot.FullName, file)}:{GetLineNumber(source, call.StartIndex)} " +
                        $"{call.MethodName}({call.Arguments.Trim()})");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "ResponsibleOfficeCode 기반 직접 권한 검사는 ResponsibleOfficeCode 공백 데이터가 담당 지점에서 누락되지 않도록 owner OfficeCode fallback 인자를 함께 전달해야 합니다."
            + Environment.NewLine
            + string.Join(Environment.NewLine, violations));
    }

    private static IEnumerable<ScopeMethodCall> FindMethodCalls(string source)
    {
        foreach (var methodName in GuardedMethods)
        {
            var searchIndex = 0;
            while (searchIndex < source.Length)
            {
                var methodIndex = source.IndexOf(methodName, searchIndex, StringComparison.Ordinal);
                if (methodIndex < 0)
                    break;

                searchIndex = methodIndex + methodName.Length;

                if (HasIdentifierCharacterBefore(source, methodIndex))
                    continue;

                var openParenIndex = SkipWhitespace(source, methodIndex + methodName.Length);
                if (openParenIndex >= source.Length || source[openParenIndex] != '(')
                    continue;

                var closeParenIndex = FindMatchingParen(source, openParenIndex);
                if (closeParenIndex < 0)
                    continue;

                searchIndex = closeParenIndex + 1;
                yield return new ScopeMethodCall(
                    methodName,
                    methodIndex,
                    source[(openParenIndex + 1)..closeParenIndex]);
            }
        }
    }

    private static bool HasIdentifierCharacterBefore(string source, int index)
    {
        return index > 0 && IsIdentifierCharacter(source[index - 1]);
    }

    private static bool IsIdentifierCharacter(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
            index++;

        return index;
    }

    private static int FindMatchingParen(string source, int openParenIndex)
    {
        var depth = 0;
        var inString = false;
        var inCharacter = false;
        var escapeNext = false;

        for (var index = openParenIndex; index < source.Length; index++)
        {
            var current = source[index];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inString)
            {
                if (current == '\\')
                    escapeNext = true;
                else if (current == '"')
                    inString = false;

                continue;
            }

            if (inCharacter)
            {
                if (current == '\\')
                    escapeNext = true;
                else if (current == '\'')
                    inCharacter = false;

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '\'')
            {
                inCharacter = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
                continue;
            }

            if (current != ')')
                continue;

            depth--;
            if (depth == 0)
                return index;
        }

        return -1;
    }

    private static IReadOnlyList<string> SplitTopLevelArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
            return [];

        var result = new List<string>();
        var startIndex = 0;
        var depth = 0;
        var inString = false;
        var inCharacter = false;
        var escapeNext = false;

        for (var index = 0; index < arguments.Length; index++)
        {
            var current = arguments[index];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (inString)
            {
                if (current == '\\')
                    escapeNext = true;
                else if (current == '"')
                    inString = false;

                continue;
            }

            if (inCharacter)
            {
                if (current == '\\')
                    escapeNext = true;
                else if (current == '\'')
                    inCharacter = false;

                continue;
            }

            if (current == '"')
            {
                inString = true;
                continue;
            }

            if (current == '\'')
            {
                inCharacter = true;
                continue;
            }

            switch (current)
            {
                case '(':
                case '[':
                case '{':
                    depth++;
                    break;
                case ')':
                case ']':
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(arguments[startIndex..index].Trim());
                    startIndex = index + 1;
                    break;
            }
        }

        result.Add(arguments[startIndex..].Trim());
        return result.Where(argument => argument.Length > 0).ToList();
    }

    private static int GetLineNumber(string source, int index)
    {
        var line = 1;
        for (var current = 0; current < index && current < source.Length; current++)
        {
            if (source[current] == '\n')
                line++;
        }

        return line;
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (directory.GetFiles("*.sln").Any())
                return directory;

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Repository root containing a solution file was not found.");
    }

    private sealed record ScopeMethodCall(string MethodName, int StartIndex, string Arguments);
}
