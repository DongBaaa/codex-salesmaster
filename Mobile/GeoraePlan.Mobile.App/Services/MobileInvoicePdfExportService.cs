using System.Globalization;
using System.Text;
using GeoraePlan.Mobile.App.Models;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.ApplicationModel.DataTransfer;
using Microsoft.Maui.Storage;
using 거래플랜.Shared.Contracts;

namespace GeoraePlan.Mobile.App.Services;

public sealed class MobileInvoicePdfExportService
{
    public async Task<string> ExportAndShareAsync(
        InvoiceDto invoice,
        MobileInvoicePrintOptions options,
        CancellationToken ct = default)
    {
        if (!options.HasAnyDocument)
            throw new InvalidOperationException("PDF로 저장할 출력 서류를 하나 이상 선택하세요.");

        var pages = BuildPages(invoice, options);
        var pdfBytes = SimpleKoreanPdfWriter.Build(pages);
        var root = Path.Combine(FileSystem.AppDataDirectory, "invoice-pdf");
        Directory.CreateDirectory(root);

        var kind = invoice.VoucherType == VoucherType.Purchase ? "purchase-invoice" : "sales-invoice";
        var customer = SanitizePortableFileName(invoice.CustomerName);
        var fileName = string.IsNullOrWhiteSpace(customer)
            ? $"{DateTime.Now:yyyyMMdd-HHmmss}_{kind}.pdf"
            : $"{DateTime.Now:yyyyMMdd-HHmmss}_{kind}_{customer}.pdf";
        var path = Path.Combine(root, fileName);
        await File.WriteAllBytesAsync(path, pdfBytes, ct);

        try
        {
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = "전표 PDF 저장/공유",
                File = new ShareFile(path, "application/pdf")
            });
        }
        catch
        {
            await Launcher.Default.OpenAsync(new OpenFileRequest("전표 PDF", new ReadOnlyFile(path, "application/pdf")));
        }

        return path;
    }

    private static List<PdfPageContent> BuildPages(InvoiceDto invoice, MobileInvoicePrintOptions options)
    {
        var pages = new List<PdfPageContent>();
        if (options.PrintStatementDocument)
            pages.Add(BuildDocumentPage(invoice, options, "거래명세서"));
        if (options.PrintEstimateDocument)
            pages.Add(BuildDocumentPage(invoice, options, "견적서"));
        if (options.PrintPaymentClaimDocument)
            pages.Add(BuildDocumentPage(invoice, options, "대금청구서"));

        return pages;
    }

    private static PdfPageContent BuildDocumentPage(InvoiceDto invoice, MobileInvoicePrintOptions options, string title)
    {
        var content = new PdfPageContent(title);
        var y = 790;
        var documentKind = invoice.VoucherType == VoucherType.Purchase ? "구매(매입)" : "판매(매출)";
        var number = string.IsNullOrWhiteSpace(invoice.InvoiceNumber)
            ? string.IsNullOrWhiteSpace(invoice.LocalTempNumber) ? "-" : invoice.LocalTempNumber
            : invoice.InvoiceNumber;

        content.Center(title, 18, y);
        y -= 34;
        content.Text($"전표구분: {documentKind}", 54, y, 10);
        content.Text($"전표번호: {number}", 300, y, 10);
        y -= 20;
        content.Text($"거래처: {invoice.CustomerName}", 54, y, 10);
        if (options.PrintDate)
            content.Text($"전표일자: {invoice.InvoiceDate:yyyy-MM-dd}", 300, y, 10);
        y -= 18;
        content.Text($"메모: {Blank(invoice.Memo)}", 54, y, 9);

        y -= 22;
        content.Line(54, y, 540, y);
        y -= 16;
        content.Text("NO", 58, y, 9);
        content.Text("품명", 88, y, 9);
        content.Text("규격", 250, y, 9);
        content.Text("수량", 355, y, 9);
        content.Text("단위", 395, y, 9);
        if (options.PrintUnitPrice)
            content.Text("단가", 435, y, 9);
        content.Text("금액", 500, y, 9);
        y -= 8;
        content.Line(54, y, 540, y);
        y -= 18;

        var lines = invoice.Lines
            .Where(line => !line.IsDeleted)
            .OrderBy(line => line.OrderIndex > 0 ? line.OrderIndex : int.MaxValue)
            .ThenBy(line => line.Id)
            .ToList();
        for (var index = 0; index < Math.Min(lines.Count, 15); index++)
        {
            var line = lines[index];
            content.Text((index + 1).ToString(CultureInfo.InvariantCulture), 58, y, 9);
            content.Text(Trim(line.ItemNameOriginal, 18), 88, y, 9);
            content.Text(Trim(line.SpecificationOriginal, 14), 250, y, 9);
            content.Right(line.Quantity.ToString("N0", CultureInfo.CurrentCulture), 382, y, 9);
            content.Text(string.IsNullOrWhiteSpace(line.Unit) ? "EA" : line.Unit, 395, y, 9);
            if (options.PrintUnitPrice)
                content.Right(line.UnitPrice.ToString("N0", CultureInfo.CurrentCulture), 482, y, 9);
            content.Right(line.LineAmount.ToString("N0", CultureInfo.CurrentCulture), 538, y, 9);
            y -= 20;
        }

        y = Math.Max(y, 154);
        content.Line(54, y, 540, y);
        y -= 22;
        content.Right($"공급가 {invoice.SupplyAmount:N0}원", 540, y, 11);
        y -= 20;
        content.Right($"부가세 {invoice.VatAmount:N0}원", 540, y, 11);
        y -= 22;
        content.Right($"합계 {invoice.TotalAmount:N0}원", 540, y, 13);

        if (title == "대금청구서")
        {
            y -= 32;
            content.Text("위 금액을 청구합니다.", 54, y, 11);
        }

        content.Text("거래플랜 모바일 PDF", 54, 46, 8);
        content.Right(DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture), 540, 46, 8);
        return content;
    }

    private static string Blank(string? value)
        => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

    private static string Trim(string? value, int maxLength)
    {
        var text = Blank(value);
        return text.Length <= maxLength ? text : text[..Math.Max(0, maxLength - 1)] + "…";
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
            builder.Append(invalid.Contains(ch) ? '_' : ch);

        var result = builder.ToString().Trim();
        return string.IsNullOrWhiteSpace(result) ? "전표" : result;
    }

    private static string SanitizePortableFileName(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "customer" : value.Trim();
        var builder = new StringBuilder(source.Length);
        foreach (var ch in source)
        {
            if (char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')
                builder.Append(ch);
            else if (char.IsWhiteSpace(ch))
                builder.Append('_');
        }

        var result = builder.ToString().Trim('_', '.', '-');
        return string.IsNullOrWhiteSpace(result) ? "customer" : result;
    }

    private sealed class PdfPageContent
    {
        private readonly StringBuilder _content = new();

        public PdfPageContent(string title)
        {
            Title = title;
        }

        public string Title { get; }

        public string Content => _content.ToString();

        public void Text(string text, int x, int y, int size)
            => _content.Append("BT /F1 ")
                .Append(size.ToString(CultureInfo.InvariantCulture))
                .Append(" Tf ")
                .Append(x.ToString(CultureInfo.InvariantCulture))
                .Append(' ')
                .Append(y.ToString(CultureInfo.InvariantCulture))
                .Append(" Td ")
                .Append(ToPdfHex(text))
                .Append(" Tj ET\n");

        public void Center(string text, int size, int y)
        {
            var estimatedWidth = text.Length * size * 0.5;
            Text(text, (int)Math.Max(54, 297 - estimatedWidth / 2), y, size);
        }

        public void Right(string text, int rightX, int y, int size)
        {
            var estimatedWidth = text.Length * size * 0.52;
            Text(text, (int)Math.Max(54, rightX - estimatedWidth), y, size);
        }

        public void Line(int x1, int y1, int x2, int y2)
            => _content.Append("0.7 w ")
                .Append(x1.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(y1.ToString(CultureInfo.InvariantCulture)).Append(" m ")
                .Append(x2.ToString(CultureInfo.InvariantCulture)).Append(' ')
                .Append(y2.ToString(CultureInfo.InvariantCulture)).Append(" l S\n");

        private static string ToPdfHex(string? text)
        {
            var bytes = Encoding.BigEndianUnicode.GetBytes(text ?? string.Empty);
            return "<FEFF" + Convert.ToHexString(bytes) + ">";
        }
    }

    private static class SimpleKoreanPdfWriter
    {
        public static byte[] Build(IReadOnlyList<PdfPageContent> pages)
        {
            var objects = new List<byte[]> { Array.Empty<byte>() };
            var pageObjectIds = new List<int>();
            const int catalogId = 1;
            const int pagesId = 2;
            const int fontId = 3;

            objects.Add(EncodeObject("<< /Type /Catalog /Pages 2 0 R >>"));
            objects.Add(Array.Empty<byte>());
            objects.Add(EncodeObject("<< /Type /Font /Subtype /Type0 /BaseFont /HYGoThic-Medium /Encoding /UniKS-UCS2-H /DescendantFonts [4 0 R] >>"));
            objects.Add(EncodeObject("<< /Type /Font /Subtype /CIDFontType0 /BaseFont /HYGoThic-Medium /CIDSystemInfo << /Registry (Adobe) /Ordering (Korea1) /Supplement 2 >> /FontDescriptor 5 0 R /DW 1000 >>"));
            objects.Add(EncodeObject("<< /Type /FontDescriptor /FontName /HYGoThic-Medium /Flags 4 /FontBBox [-1000 -1000 1000 1000] /ItalicAngle 0 /Ascent 880 /Descent -120 /CapHeight 880 /StemV 80 >>"));

            foreach (var page in pages)
            {
                var pageId = objects.Count;
                var contentId = pageId + 1;
                pageObjectIds.Add(pageId);
                objects.Add(EncodeObject($"<< /Type /Page /Parent {pagesId} 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 {fontId} 0 R >> >> /Contents {contentId} 0 R >>"));
                var contentBytes = Encoding.ASCII.GetBytes(page.Content);
                objects.Add(EncodeObject($"<< /Length {contentBytes.Length} >>\nstream\n{page.Content}endstream"));
            }

            var kids = string.Join(" ", pageObjectIds.Select(id => $"{id} 0 R"));
            objects[pagesId] = EncodeObject($"<< /Type /Pages /Kids [{kids}] /Count {pageObjectIds.Count} >>");

            using var stream = new MemoryStream();
            WriteAscii(stream, "%PDF-1.4\n%거래플랜\n");
            var offsets = new List<long> { 0 };
            for (var id = 1; id < objects.Count; id++)
            {
                offsets.Add(stream.Position);
                WriteAscii(stream, $"{id} 0 obj\n");
                stream.Write(objects[id], 0, objects[id].Length);
                WriteAscii(stream, "\nendobj\n");
            }

            var xrefOffset = stream.Position;
            WriteAscii(stream, $"xref\n0 {objects.Count}\n");
            WriteAscii(stream, "0000000000 65535 f \n");
            for (var id = 1; id < objects.Count; id++)
                WriteAscii(stream, $"{offsets[id]:0000000000} 00000 n \n");
            WriteAscii(stream, $"trailer\n<< /Size {objects.Count} /Root {catalogId} 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
            return stream.ToArray();
        }

        private static byte[] EncodeObject(string value)
            => Encoding.ASCII.GetBytes(value);

        private static void WriteAscii(Stream stream, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            stream.Write(bytes, 0, bytes.Length);
        }
    }
}
