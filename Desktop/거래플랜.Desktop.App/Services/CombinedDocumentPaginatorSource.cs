using System.Windows;
using System.Windows.Documents;

namespace 거래플랜.Desktop.App.Services;

internal sealed class CombinedDocumentPaginatorSource : IDocumentPaginatorSource
{
    private readonly CombinedDocumentPaginator _paginator;

    public CombinedDocumentPaginatorSource(IEnumerable<IDocumentPaginatorSource> documents)
    {
        var sources = documents?
            .Where(static document => document is not null)
            .ToList() ?? [];

        if (sources.Count == 0)
            throw new InvalidOperationException("병합할 문서가 없습니다.");

        _paginator = new CombinedDocumentPaginator(this, sources);
    }

    public DocumentPaginator DocumentPaginator => _paginator;

    private sealed class CombinedDocumentPaginator : DocumentPaginator
    {
        private readonly IDocumentPaginatorSource _owner;
        private readonly IReadOnlyList<DocumentPaginator> _paginators;
        private Size _pageSize;

        public CombinedDocumentPaginator(IDocumentPaginatorSource owner, IReadOnlyList<IDocumentPaginatorSource> sources)
        {
            _owner = owner;
            _paginators = sources.Select(static source => source.DocumentPaginator).ToList();
            _pageSize = ResolveDefaultPageSize(_paginators);
            ApplyPageSize(_pageSize);
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            if (pageNumber < 0)
                return DocumentPage.Missing;

            var pageIndex = pageNumber;
            foreach (var paginator in _paginators)
            {
                var count = paginator.PageCount;
                if (count <= 0)
                    continue;

                if (pageIndex < count)
                    return paginator.GetPage(pageIndex);

                pageIndex -= count;
            }

            return DocumentPage.Missing;
        }

        public override bool IsPageCountValid => _paginators.All(static paginator => paginator.IsPageCountValid);

        public override int PageCount => _paginators.Sum(static paginator => Math.Max(0, paginator.PageCount));

        public override Size PageSize
        {
            get => _pageSize;
            set
            {
                _pageSize = value;
                ApplyPageSize(value);
            }
        }

        public override IDocumentPaginatorSource Source => _owner;

        private void ApplyPageSize(Size pageSize)
        {
            foreach (var paginator in _paginators)
                paginator.PageSize = pageSize;
        }

        private static Size ResolveDefaultPageSize(IEnumerable<DocumentPaginator> paginators)
        {
            foreach (var paginator in paginators)
            {
                if (paginator.PageSize.Width > 0 && paginator.PageSize.Height > 0)
                    return paginator.PageSize;
            }

            return new Size(793.7, 1122.5);
        }
    }
}
