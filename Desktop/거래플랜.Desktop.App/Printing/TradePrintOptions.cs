using System.Printing;

namespace 거래플랜.Desktop.App.Printing;

public sealed record TradePrintDialogResult(
    PrintQueue? PrintQueue,
    int CopyCount,
    bool Collate,
    IReadOnlyList<int>? PageNumbers,
    bool ReversePageOrder,
    bool SaveToFile = false,
    string? OutputFilePath = null);

public static class TradePrintPageRangeParser
{
    public static bool TryParse(
        string? input,
        int pageCount,
        out IReadOnlyList<int> pageNumbers,
        out string? errorMessage)
    {
        pageNumbers = Array.Empty<int>();
        errorMessage = null;

        if (pageCount <= 0)
        {
            errorMessage = "문서 페이지 수를 확인할 수 없어 페이지 범위를 지정할 수 없습니다.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            errorMessage = "인쇄할 페이지 번호나 범위를 입력하세요. 예: 1,3,5-12";
            return false;
        }

        var orderedPages = new List<int>();
        var seenPages = new HashSet<int>();
        var tokens = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            errorMessage = "인쇄할 페이지 번호나 범위를 입력하세요. 예: 1,3,5-12";
            return false;
        }

        foreach (var token in tokens)
        {
            var dashIndex = token.IndexOf('-', StringComparison.Ordinal);
            if (dashIndex >= 0)
            {
                if (dashIndex == 0 || dashIndex == token.Length - 1 || token.IndexOf('-', dashIndex + 1) >= 0)
                {
                    errorMessage = $"페이지 범위 형식이 올바르지 않습니다: {token}";
                    return false;
                }

                if (!TryReadPageNumber(token[..dashIndex], pageCount, out var startPage, out errorMessage) ||
                    !TryReadPageNumber(token[(dashIndex + 1)..], pageCount, out var endPage, out errorMessage))
                {
                    return false;
                }

                if (startPage > endPage)
                {
                    errorMessage = $"페이지 범위의 시작 번호가 끝 번호보다 큽니다: {token}";
                    return false;
                }

                for (var page = startPage; page <= endPage; page++)
                    AddPage(page);

                continue;
            }

            if (!TryReadPageNumber(token, pageCount, out var singlePage, out errorMessage))
                return false;

            AddPage(singlePage);
        }

        if (orderedPages.Count == 0)
        {
            errorMessage = "인쇄할 페이지 번호나 범위를 입력하세요. 예: 1,3,5-12";
            return false;
        }

        pageNumbers = orderedPages;
        return true;

        void AddPage(int page)
        {
            if (seenPages.Add(page))
                orderedPages.Add(page);
        }
    }

    private static bool TryReadPageNumber(
        string rawValue,
        int pageCount,
        out int pageNumber,
        out string? errorMessage)
    {
        pageNumber = 0;
        errorMessage = null;

        var value = rawValue.Trim();
        if (!int.TryParse(value, out pageNumber))
        {
            errorMessage = $"페이지 번호는 숫자로 입력하세요: {value}";
            return false;
        }

        if (pageNumber < 1)
        {
            errorMessage = "페이지 번호는 1 이상이어야 합니다.";
            return false;
        }

        if (pageNumber > pageCount)
        {
            errorMessage = $"문서는 총 {pageCount}쪽입니다. {pageNumber}쪽은 인쇄할 수 없습니다.";
            return false;
        }

        return true;
    }
}
