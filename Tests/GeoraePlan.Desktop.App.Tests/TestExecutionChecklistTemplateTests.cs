using Xunit;

namespace GeoraePlan.Desktop.App.Tests;

public sealed class TestExecutionChecklistTemplateTests
{
    [Fact]
    public void ChecklistTemplateRequiresOfficeWarehouseRentalAndAndroidManualQaEvidence()
    {
        var template = ReadRepositoryFile("테스트 시행", "검증 체크리스트 템플릿.md");

        Assert.Contains("## 7. 계정/지점/창고 범위 수동 QA", template, StringComparison.Ordinal);
        Assert.Contains("ITWORLD / USENET / YEONSU", template, StringComparison.Ordinal);
        Assert.Contains("거래처 검색/선택 목록", template, StringComparison.Ordinal);
        Assert.Contains("출고창고 선택", template, StringComparison.Ordinal);
        Assert.Contains("dirty 상태, 서버 반영, 재조회 결과", template, StringComparison.Ordinal);
        Assert.Contains("재고이동 화면에서 출고창고/입고창고", template, StringComparison.Ordinal);
        Assert.Contains("렌탈 청구관리와 렌탈 자산/설치현황", template, StringComparison.Ordinal);
        Assert.Contains("내부 포함 장비, 청구서 표시 품목", template, StringComparison.Ordinal);

        Assert.Contains("## 8. Android 모바일 수동 QA", template, StringComparison.Ordinal);
        Assert.Contains("Android 실기기 또는 에뮬레이터", template, StringComparison.Ordinal);
        Assert.Contains("로그인 → 거래처 조회 → 품목 조회 → 전표 작성 → 동기화", template, StringComparison.Ordinal);
        Assert.Contains("권한 없는 거래처/전표/재고/렌탈 데이터", template, StringComparison.Ordinal);
        Assert.Contains("BLOCKED/오류 상태", template, StringComparison.Ordinal);
    }

    private static string ReadRepositoryFile(params string[] pathParts)
        => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. pathParts]));

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Repository root was not found.");
    }
}
