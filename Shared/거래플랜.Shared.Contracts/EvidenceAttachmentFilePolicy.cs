namespace 거래플랜.Shared.Contracts;

public static class EvidenceAttachmentFilePolicy
{
    public const long MaxFileSizeBytes = 15L * 1024 * 1024;

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic", ".heif"
    };

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/png",
        "image/jpeg",
        "image/bmp",
        "image/gif",
        "image/webp",
        "image/tiff",
        "image/heic",
        "image/heif"
    };

    public static bool IsAllowedFileType(string? fileName, string? contentType)
    {
        var safeFileName = Path.GetFileName(fileName ?? string.Empty);
        var extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrWhiteSpace(safeFileName) || !AllowedExtensions.Contains(extension))
            return false;

        var normalizedContentType = NormalizeContentType(contentType, safeFileName);
        if (AllowedContentTypes.Contains(normalizedContentType))
            return true;

        return string.Equals(normalizedContentType, "application/octet-stream", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(extension, ".pdf", StringComparison.OrdinalIgnoreCase);
    }

    public static bool ContentMatchesFileType(string? fileName, string? contentType, byte[]? content)
    {
        if (content is null || content.Length == 0)
            return false;

        var safeFileName = Path.GetFileName(fileName ?? string.Empty);
        var kind = ResolveKindByExtension(safeFileName);
        if (kind is null)
            return false;

        return kind.Value switch
        {
            EvidenceAttachmentKind.Pdf => LooksLikePdf(content),
            EvidenceAttachmentKind.Png => StartsWith(content, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            EvidenceAttachmentKind.Jpeg => content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF,
            EvidenceAttachmentKind.Bmp => StartsWith(content, [0x42, 0x4D]),
            EvidenceAttachmentKind.Gif => StartsWithAscii(content, "GIF87a") || StartsWithAscii(content, "GIF89a"),
            EvidenceAttachmentKind.Webp => LooksLikeWebp(content),
            EvidenceAttachmentKind.Tiff => LooksLikeTiff(content),
            EvidenceAttachmentKind.Heif => LooksLikeHeif(content),
            _ => false
        };
    }

    public static string NormalizeContentType(string? contentType, string fileName)
    {
        if (!string.IsNullOrWhiteSpace(contentType))
            return contentType.Split(';', 2, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)[0].Trim();

        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".heic" => "image/heic",
            ".heif" => "image/heif",
            _ => "application/octet-stream"
        };
    }

    private static EvidenceAttachmentKind? ResolveKindByExtension(string? fileName)
        => Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant() switch
        {
            ".pdf" => EvidenceAttachmentKind.Pdf,
            ".png" => EvidenceAttachmentKind.Png,
            ".jpg" or ".jpeg" => EvidenceAttachmentKind.Jpeg,
            ".bmp" => EvidenceAttachmentKind.Bmp,
            ".gif" => EvidenceAttachmentKind.Gif,
            ".webp" => EvidenceAttachmentKind.Webp,
            ".tif" or ".tiff" => EvidenceAttachmentKind.Tiff,
            ".heic" or ".heif" => EvidenceAttachmentKind.Heif,
            _ => null
        };

    private static bool LooksLikePdf(byte[] content)
    {
        var limit = Math.Min(content.Length - 4, 1024);
        for (var index = 0; index <= limit; index++)
        {
            if (content[index] == (byte)'%' &&
                content[index + 1] == (byte)'P' &&
                content[index + 2] == (byte)'D' &&
                content[index + 3] == (byte)'F' &&
                content[index + 4] == (byte)'-')
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeWebp(byte[] content)
        => content.Length >= 12 &&
           StartsWithAscii(content, "RIFF") &&
           content[8] == (byte)'W' &&
           content[9] == (byte)'E' &&
           content[10] == (byte)'B' &&
           content[11] == (byte)'P';

    private static bool LooksLikeTiff(byte[] content)
        => StartsWith(content, [0x49, 0x49, 0x2A, 0x00]) ||
           StartsWith(content, [0x4D, 0x4D, 0x00, 0x2A]);

    private static bool LooksLikeHeif(byte[] content)
    {
        if (content.Length < 12 ||
            content[4] != (byte)'f' ||
            content[5] != (byte)'t' ||
            content[6] != (byte)'y' ||
            content[7] != (byte)'p')
        {
            return false;
        }

        for (var offset = 8; offset + 3 < content.Length && offset < 64; offset += 4)
        {
            if (MatchesBrand(content, offset, "heic") ||
                MatchesBrand(content, offset, "heif") ||
                MatchesBrand(content, offset, "heix") ||
                MatchesBrand(content, offset, "hevc") ||
                MatchesBrand(content, offset, "hevx") ||
                MatchesBrand(content, offset, "mif1") ||
                MatchesBrand(content, offset, "msf1"))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartsWith(byte[] content, ReadOnlySpan<byte> prefix)
    {
        if (content.Length < prefix.Length)
            return false;

        for (var index = 0; index < prefix.Length; index++)
        {
            if (content[index] != prefix[index])
                return false;
        }

        return true;
    }

    private static bool StartsWithAscii(byte[] content, string text)
    {
        if (content.Length < text.Length)
            return false;

        for (var index = 0; index < text.Length; index++)
        {
            if (content[index] != (byte)text[index])
                return false;
        }

        return true;
    }

    private static bool MatchesBrand(byte[] content, int offset, string brand)
    {
        if (offset + brand.Length > content.Length)
            return false;

        for (var index = 0; index < brand.Length; index++)
        {
            if (content[offset + index] != (byte)brand[index])
                return false;
        }

        return true;
    }

    private enum EvidenceAttachmentKind
    {
        Pdf,
        Png,
        Jpeg,
        Bmp,
        Gif,
        Webp,
        Tiff,
        Heif
    }
}
