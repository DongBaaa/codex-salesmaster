using System.Text.RegularExpressions;

namespace 거래플랜.Shared.Contracts;

public static class MobileDiagnosticTextRedactor
{
    private static readonly Regex AbsoluteUrlPattern = new(
        @"https?://[^\s]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex WindowsPathPattern = new(
        @"[A-Za-z]:\\[^\s]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex UnixPathPattern = new(
        @"(^|\s)(/[A-Za-z0-9_\-./]+/[A-Za-z0-9_\-./]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex FileNamePattern = new(
        @"(?<![/\\])\b[^\s/\\:*?""<>|]+\.(pdf|png|jpg|jpeg|gif|bmp|webp|heic|csv|txt|zip|xlsx|xls|docx)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex SecretAssignmentPattern = new(
        @"\b(?<key>authorization|bearer|access[_-]?token|refresh[_-]?token|token|password|secret|api[_-]?key)\b\s*[:=]?\s*(?:bearer\s+)?[""']?(?<secret>[^,;\s""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JwtPattern = new(
        @"\beyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string SanitizeFreeText(string? value)
    {
        var sanitized = (value ?? string.Empty)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " / ", StringComparison.Ordinal)
            .Trim();

        if (string.IsNullOrWhiteSpace(sanitized))
            return "-";

        sanitized = AbsoluteUrlPattern.Replace(sanitized, match => SanitizeAbsoluteUrl(match.Value));
        sanitized = SecretAssignmentPattern.Replace(
            sanitized,
            match => $"{match.Groups["key"].Value}=[비밀값 숨김]");
        sanitized = JwtPattern.Replace(sanitized, "[토큰 숨김]");
        sanitized = WindowsPathPattern.Replace(sanitized, "[경로 숨김]");
        sanitized = UnixPathPattern.Replace(
            sanitized,
            match => match.Groups[1].Value + "[경로 숨김]");
        sanitized = FileNamePattern.Replace(sanitized, "[파일명 숨김]");
        sanitized = Regex.Replace(sanitized, @"\s{2,}", " ");
        return sanitized.Trim();
    }

    private static string SanitizeAbsoluteUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl.TrimEnd('.', ',', ';', ')'), UriKind.Absolute, out var uri))
            return "[URL 숨김]";

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty,
            Path = "/"
        };

        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
