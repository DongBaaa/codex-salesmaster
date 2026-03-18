using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using 거래플랜.Desktop.App.Data;

namespace 거래플랜.Desktop.App.Services;

/// <summary>
/// Generates 거래명세서 (Korean trade statement) as A4 PDF.
/// Layout: 1 page, 2 copies top/bottom, up to 13 line items each.
/// </summary>
public sealed class StatementPrintService
{
    static StatementPrintService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] GeneratePdf(LocalInvoice invoice, LocalCustomer customer, LocalCompanyProfile company)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(10, Unit.Millimetre);
                page.DefaultTextStyle(x => x.FontFamily("Malgun Gothic").FontSize(8));

                page.Content().Column(col =>
                {
                    // Top copy
                    col.Item().Element(c => ComposeStatement(c, invoice, customer, company, isCopy: false));
                    // Separator
                    col.Item().Height(4).Background("#CCCCCC");
                    // Bottom copy (carbon copy)
                    col.Item().Element(c => ComposeStatement(c, invoice, customer, company, isCopy: true));
                });
            });
        }).GeneratePdf();
    }

    private static void ComposeStatement(
        IContainer container,
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool isCopy)
    {
        container.Border(1).Padding(4).Column(col =>
        {
            // ── Title ──────────────────────────────────────────────────────
            col.Item().AlignCenter().Text(isCopy ? "거래명세서 (보관용)" : "거래명세서")
                .FontSize(14).Bold();

            col.Item().PaddingTop(2).Row(row =>
            {
                // Left: customer info
                row.RelativeItem().Column(left =>
                {
                    left.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("거래처:").Bold();
                        r.RelativeItem().Text(customer.NameOriginal);
                    });
                    left.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("사업자:").Bold();
                        r.RelativeItem().Text(customer.BusinessNumber);
                    });
                    left.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("주소:").Bold();
                        r.RelativeItem().Text(customer.Address);
                    });
                    left.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("전화:").Bold();
                        r.RelativeItem().Text(customer.Phone);
                    });
                });

                // Right: company info + stamp area
                row.RelativeItem().Column(right =>
                {
                    right.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("상호:").Bold();
                        r.RelativeItem().Text(company.TradeName);
                    });
                    right.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("대표자:").Bold();
                        r.RelativeItem().Text(company.Representative);
                    });
                    right.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("사업자번호:").Bold();
                        r.RelativeItem().Text(company.BusinessNumber);
                    });
                    right.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("연락처:").Bold();
                        r.RelativeItem().Text(company.ContactNumber);
                    });
                    right.Item().Row(r =>
                    {
                        r.ConstantItem(40).Text("이메일:").Bold();
                        r.RelativeItem().Text(company.Email);
                    });
                    // Stamp image placeholder
                    right.Item().Height(30).AlignRight().Element(stampArea =>
                    {
                        if (company.StampImage is { Length: > 0 } stamp)
                            stampArea.Image(stamp);
                        else
                            stampArea.Border(1).AlignCenter().AlignMiddle()
                                .Text("(인)").FontSize(9).Italic();
                    });
                });
            });

            // Invoice header info
            col.Item().PaddingTop(2).Row(row =>
            {
                row.RelativeItem().Text($"전표번호: {invoice.InvoiceNumber ?? invoice.LocalTempNumber}");
                row.RelativeItem().Text($"작성일자: {invoice.InvoiceDate:yyyy년 MM월 dd일}");
                row.RelativeItem().Text($"전표유형: {invoice.VoucherType}");
            });

            // ── Line items table ───────────────────────────────────────────
            col.Item().PaddingTop(3).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(20);  // No.
                    c.RelativeColumn(3);   // 품명
                    c.RelativeColumn(2);   // 규격
                    c.ConstantColumn(22);  // 단위
                    c.RelativeColumn(1.2f);// 수량
                    c.RelativeColumn(2);   // 단가
                    c.RelativeColumn(2);   // 금액
                    c.RelativeColumn(2);   // 비고
                });

                // Header row
                static IContainer HeaderCell(IContainer c) =>
                    c.Background("#1A2B4A").Padding(2).AlignCenter();

                table.Header(h =>
                {
                    h.Cell().Element(HeaderCell).Text("No.").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("품명").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("규격").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("단위").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("수량").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("단가").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("금액").FontColor("#FFFFFF").Bold();
                    h.Cell().Element(HeaderCell).Text("비고").FontColor("#FFFFFF").Bold();
                });

                var lines = invoice.Lines.Where(l => !l.IsDeleted).Take(13).ToList();
                for (var i = 0; i < 13; i++)
                {
                    var line = i < lines.Count ? lines[i] : null;
                    var bg = i % 2 == 0 ? "#FFFFFF" : "#F5F7FA";

                    static IContainer DataCell(IContainer c, string bg) =>
                        c.Background(bg).Padding(2);

                    table.Cell().Element(c => DataCell(c, bg)).AlignCenter().Text(line is not null ? (i + 1).ToString() : "");
                    table.Cell().Element(c => DataCell(c, bg)).Text(line?.ItemNameOriginal ?? "");
                    table.Cell().Element(c => DataCell(c, bg)).Text(line?.SpecificationOriginal ?? "");
                    table.Cell().Element(c => DataCell(c, bg)).AlignCenter().Text(line?.Unit ?? "");
                    table.Cell().Element(c => DataCell(c, bg)).AlignRight().Text(line is not null ? $"{line.Quantity:N0}" : "");
                    table.Cell().Element(c => DataCell(c, bg)).AlignRight().Text(line is not null ? $"{line.UnitPrice:N0}" : "");
                    table.Cell().Element(c => DataCell(c, bg)).AlignRight().Text(line is not null ? $"{line.LineAmount:N0}" : "");
                    table.Cell().Element(c => DataCell(c, bg)).Text(line?.Remark ?? "");
                }
            });

            // ── Totals ──────────────────────────────────────────────────────
            col.Item().PaddingTop(3).AlignRight().Column(totals =>
            {
                totals.Item().Row(r =>
                {
                    r.ConstantItem(60).AlignRight().Text("공급가액:").Bold();
                    r.ConstantItem(70).AlignRight().Text($"{invoice.SupplyAmount:N0} 원");
                });
                totals.Item().Row(r =>
                {
                    r.ConstantItem(60).AlignRight().Text("부가세:").Bold();
                    r.ConstantItem(70).AlignRight().Text($"{invoice.VatAmount:N0} 원");
                });
                totals.Item().Row(r =>
                {
                    r.ConstantItem(60).AlignRight().Text("합계:").Bold().FontSize(10);
                    r.ConstantItem(70).AlignRight().Text($"{invoice.TotalAmount:N0} 원").Bold().FontSize(10);
                });
            });

            // ── Bank account ────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(company.BankAccountText))
            {
                col.Item().PaddingTop(2).Background("#F0F4FA").Padding(3)
                    .Text($"입금계좌: {company.BankAccountText}").FontSize(8);
            }
        });
    }
}
