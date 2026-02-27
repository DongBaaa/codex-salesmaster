using System.Data;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using 외부 리포팅 도구;
using SalesMaster.Desktop.App.Data;

namespace SalesMaster.Desktop.App.Services;

public sealed class 외부 리포팅 도구TemplatePrintService
{
    private static readonly Regex DataFieldQuotedRegex = new(
        @"\[(?<dataset>[^\.\[\]]+)\.""(?<field>[^""\]]+)""\]",
        RegexOptions.Compiled);

    private static readonly Regex DataFieldSimpleRegex = new(
        @"\[(?<dataset>[^\.\[\]]+)\.(?<field>[^\]]+)\]",
        RegexOptions.Compiled);

    private static readonly Regex LegacyQuotedFieldExpressionRegex = new(
        @"\[(?<dataset>[^\.\[\]]+)\.""(?<field>[^""\]]+)""\]",
        RegexOptions.Compiled);

    private static readonly Regex LegacySimpleFieldExpressionRegex = new(
        @"\[(?<dataset>[^\.\[\]]+)\.(?<field>[^\]\r\n]+)\]",
        RegexOptions.Compiled);

    private static readonly IReadOnlyDictionary<string, string> LegacyDataSourceNameMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Top_frxDB"] = "Top_frxDB",
            ["SaeBu_frxDB"] = "SaeBu_frxDB",
            ["Go_frxDBD"] = "Go_frxDBD",
            ["SYSUSER_frxDB"] = "SYSUSER_frxDB",

            ["Top"] = "Top_frxDB",
            ["SaeBu"] = "SaeBu_frxDB",
            ["Go"] = "Go_frxDBD",
            ["SYSUSER"] = "SYSUSER_frxDB",

            ["\uC804\uD45C_\uBAA9\uB85D"] = "Top_frxDB",
            ["\uC804\uD45C_\uC138\uBD80"] = "SaeBu_frxDB",
            ["\uC804\uD45C_\uC138\uBD80\uB0B4\uC5ED"] = "SaeBu_frxDB",
            ["\uAC70\uB798\uCC98_\uC815\uBCF4"] = "Go_frxDBD",
            ["\uC790\uC0AC_\uC815\uBCF4"] = "SYSUSER_frxDB",

            ["DataModule1.kbm_frxDBDataset1"] = "Top_frxDB",
            ["Geun_IN_Form.PanTop_FDS1"] = "Top_frxDB",
            ["Geun_IN_Form.PanTop_FDS2"] = "Top_frxDB",
            ["Geun_IN_Form.SaeBu_FDS1"] = "SaeBu_frxDB",
            ["Geun_IN_Form.SGo_FDS1"] = "Go_frxDBD",
            ["Geun_IN_Form.SYSUSER_FDS1"] = "SYSUSER_frxDB"
        };

    public bool ShowPreviewAndPrint(
        string templatePath,
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice,
        string jobName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(templatePath);
        ArgumentNullException.ThrowIfNull(invoice);
        ArgumentNullException.ThrowIfNull(customer);
        ArgumentNullException.ThrowIfNull(company);

        if (!File.Exists(templatePath))
            throw new FileNotFoundException("양식 파일을 찾을 수 없습니다.", templatePath);

        Exception? lastException = null;

        foreach (var loadPath in GetTemplateLoadCandidates(templatePath))
        {
            try
            {
                var metadata = ExtractTemplateMetadata(loadPath);
                var dataSet = BuildDataSet(metadata, invoice, customer, company, printWithDate, printWithPrice);

                using var report = new Report();
                report.Load(loadPath);

                if (report.AllObjects.Count == 0 &&
                    string.Equals(Path.GetExtension(loadPath), ".fr3", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "레거시 .fr3 양식 객체를 읽지 못했습니다. 외부 리포팅 도구 Designer에서 .frx로 저장한 뒤 사용하세요.");
                }

                RegisterData(report, dataSet, metadata);
                EnsureDataBandsCanRenderWithoutRows(report);

                var prepared = report.Prepare();
                if (!prepared || report.PreparedPages is null || report.PreparedPages.Count <= 0)
                    throw new InvalidOperationException("양식 준비(Prepare)에 실패했습니다. 템플릿 데이터 바인딩을 확인하세요.");

                // Keep preview/print inside 외부 리포팅 도구 and avoid opening external PDF files.
                report.ShowPrepared();

                return true;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        throw new InvalidOperationException(
            "양식 준비(Prepare)에 실패했습니다. 템플릿 스크립트/필드 매핑을 확인하세요.",
            lastException);
    }

    private static IEnumerable<string> GetTemplateLoadCandidates(string templatePath)
    {
        var candidates = new List<string>();

        static void AddCandidate(List<string> list, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return;

            var fullPath = Path.GetFullPath(path);
            if (list.Any(existing =>
                    string.Equals(Path.GetFullPath(existing), fullPath, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            list.Add(path);
        }

        // Use the selected template first and avoid synthetic conversion fallbacks
        // that may generate blank previews.
        AddCandidate(candidates, templatePath);

        // If a user-maintained sibling .frx exists, allow it as explicit secondary fallback.
        if (string.Equals(Path.GetExtension(templatePath), ".fr3", StringComparison.OrdinalIgnoreCase))
        {
            var frxPath = Path.ChangeExtension(templatePath, ".frx");
            AddCandidate(candidates, frxPath);
        }

        return candidates;
    }

    private static string? TryConvertLegacyFr3TemplateToFrx(string templatePath)
    {
        try
        {
            return ConvertLegacyFr3TemplateToFrx(templatePath);
        }
        catch
        {
            return null;
        }
    }

    private static string ConvertLegacyFr3TemplateToFrx(string templatePath)
    {
        if (!string.Equals(Path.GetExtension(templatePath), ".fr3", StringComparison.OrdinalIgnoreCase))
            return templatePath;

        var info = new FileInfo(templatePath);
        var safeName = Path.GetFileNameWithoutExtension(templatePath);
        foreach (var c in Path.GetInvalidFileNameChars())
            safeName = safeName.Replace(c, '_');

        var key = $"{info.LastWriteTimeUtc.Ticks}_{info.Length}";
        var outDir = Path.Combine(Path.GetTempPath(), "SalesMaster", "TemplateConverted");
        Directory.CreateDirectory(outDir);

        var convertedPath = Path.Combine(outDir, $"{safeName}_{key}.frx");
        if (File.Exists(convertedPath))
            return convertedPath;

        var rawXml = ReadTemplateText(templatePath);
        var convertedXml = ConvertFr3XmlToFrxXml(rawXml);
        File.WriteAllText(convertedPath, convertedXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return convertedPath;
    }

    private static string ConvertFr3XmlToFrxXml(string rawXml)
    {
        var xdoc = XDocument.Parse(rawXml, LoadOptions.PreserveWhitespace);

        foreach (var element in xdoc.Descendants().ToList())
        {
            var localName = element.Name.LocalName;
            if (string.Equals(localName, "ScriptText", StringComparison.OrdinalIgnoreCase))
            {
                element.Remove();
                continue;
            }

            var mappedName = MapLegacyElementName(localName);
            if (!string.Equals(mappedName.LocalName, localName, StringComparison.Ordinal))
                element.Name = mappedName;

            var attributes = element.Attributes().ToList();
            foreach (var attribute in attributes)
            {
                var attributeName = attribute.Name.LocalName;

                if (attributeName.StartsWith("On", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attributeName, "ScriptText.Text", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(attributeName, "ScriptLanguage", StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Remove();
                    continue;
                }

                if (string.Equals(attributeName, "DataSetName", StringComparison.OrdinalIgnoreCase))
                {
                    attribute.Remove();
                    continue;
                }

                var targetName = attributeName;
                if (string.Equals(targetName, "DataSet", StringComparison.OrdinalIgnoreCase))
                    targetName = "DataSource";
                else if (string.Equals(targetName, "Memo.Text", StringComparison.OrdinalIgnoreCase))
                    targetName = "Text";

                var targetValue = NormalizeLegacyAttributeValue(targetName, attribute.Value);
                if (string.Equals(targetName, attributeName, StringComparison.Ordinal))
                {
                    attribute.Value = targetValue;
                    continue;
                }

                attribute.Remove();
                if (element.Attribute(targetName) is null)
                    element.SetAttributeValue(targetName, targetValue);
            }
        }

        xdoc.Declaration = new XDeclaration("1.0", "utf-8", null);

        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
               {
                   Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                   OmitXmlDeclaration = false,
                   Indent = true
               }))
        {
            xdoc.Save(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static XName MapLegacyElementName(string elementName)
    {
        return elementName switch
        {
            "TfrxReport" => "Report",
            "TfrxReportPage" => "ReportPage",
            "TfrxPageHeader" => "PageHeaderBand",
            "TfrxPageFooter" => "PageFooterBand",
            "TfrxReportTitle" => "ReportTitleBand",
            "TfrxReportSummary" => "ReportSummaryBand",
            "TfrxColumnHeader" => "ColumnHeaderBand",
            "TfrxColumnFooter" => "ColumnFooterBand",
            "TfrxGroupHeader" => "GroupHeaderBand",
            "TfrxGroupFooter" => "GroupFooterBand",
            "TfrxHeader" => "HeaderBand",
            "TfrxFooter" => "FooterBand",
            "TfrxChild" => "ChildBand",
            "TfrxMasterData" => "DataBand",
            "TfrxDetailData" => "DataBand",
            "TfrxDataBand" => "DataBand",
            "TfrxMemoView" => "TextObject",
            "TfrxPictureView" => "PictureObject",
            "TfrxShapeView" => "ShapeObject",
            "TfrxLineView" => "LineObject",
            "TfrxBarcodeView" => "BarcodeObject",
            "TfrxRichView" => "RichObject",
            "TfrxSubreport" => "SubreportObject",
            _ when elementName.StartsWith("Tfrx", StringComparison.Ordinal) &&
                   elementName.EndsWith("View", StringComparison.Ordinal) =>
                elementName[4..^4] + "Object",
            _ when elementName.StartsWith("Tfrx", StringComparison.Ordinal) => elementName[4..],
            _ => elementName
        };
    }

    private static string NormalizeLegacyAttributeValue(string attributeName, string value)
    {
        var normalized = value
            .Replace("&#34;", "\"", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal);

        if (string.Equals(attributeName, "DataSource", StringComparison.OrdinalIgnoreCase))
            return NormalizeLegacyDataSourceName(normalized);

        if (string.Equals(attributeName, "DataField", StringComparison.OrdinalIgnoreCase))
            return normalized.Trim().Trim('"');

        if (string.Equals(attributeName, "Text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(attributeName, "Filter", StringComparison.OrdinalIgnoreCase) ||
            attributeName.EndsWith("Expression", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeLegacyExpression(normalized);
        }

        return normalized;
    }

    private static string NormalizeLegacyExpression(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return expression;

        var normalized = WebUtility.HtmlDecode(expression);

        normalized = normalized.Replace(
            "[\uB85C\uACE0\uC778]",
            "[Top_frxDB.\uB85C\uACE0\uC778]",
            StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "[\uCD9C\uB825\uC77C\uC790\uC2DC\uAC04]",
            "[Top_frxDB.\uCD9C\uB825\uC77C\uC790\uC2DC\uAC04]",
            StringComparison.OrdinalIgnoreCase);

        normalized = LegacyQuotedFieldExpressionRegex.Replace(normalized, static match =>
        {
            var dataSet = NormalizeLegacyDataSourceName(match.Groups["dataset"].Value);
            var field = match.Groups["field"].Value.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(field)
                ? match.Value
                : $"[{dataSet}.{field}]";
        });

        normalized = LegacySimpleFieldExpressionRegex.Replace(normalized, static match =>
        {
            var originalDataSet = match.Groups["dataset"].Value;
            if (!ShouldNormalizeDataSetReference(originalDataSet))
                return match.Value;

            var dataSet = NormalizeLegacyDataSourceName(originalDataSet);
            var field = match.Groups["field"].Value.Trim().Trim('"');
            return string.IsNullOrWhiteSpace(field)
                ? match.Value
                : $"[{dataSet}.{field}]";
        });

        return normalized;
    }

    private static bool ShouldNormalizeDataSetReference(string dataSetName)
    {
        if (string.IsNullOrWhiteSpace(dataSetName))
            return false;

        var normalized = dataSetName.Trim().Trim('"');
        if (LegacyDataSourceNameMap.ContainsKey(normalized))
            return true;

        return normalized.Contains("Top", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("SaeBu", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("Go", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("SYSUSER", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\uC804\uD45C", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\uAC70\uB798\uCC98", StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains("\uC790\uC0AC", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLegacyDataSourceName(string dataSetName)
    {
        if (string.IsNullOrWhiteSpace(dataSetName))
            return dataSetName;

        var normalized = dataSetName.Trim().Trim('"');
        if (LegacyDataSourceNameMap.TryGetValue(normalized, out var mapped))
            return mapped;

        if (normalized.Contains("SaeBu", StringComparison.OrdinalIgnoreCase))
            return "SaeBu_frxDB";
        if (normalized.Contains("Top", StringComparison.OrdinalIgnoreCase))
            return "Top_frxDB";
        if (normalized.Contains("SYSUSER", StringComparison.OrdinalIgnoreCase))
            return "SYSUSER_frxDB";
        if (normalized.Contains("Go", StringComparison.OrdinalIgnoreCase))
            return "Go_frxDBD";

        return normalized;
    }

    private static string CreateScriptlessTemplateCopy(string templatePath)
    {
        if (!string.Equals(Path.GetExtension(templatePath), ".fr3", StringComparison.OrdinalIgnoreCase))
            return templatePath;

        var rawXml = ReadTemplateText(templatePath);
        if (!rawXml.Contains("OnBeforePrint", StringComparison.OrdinalIgnoreCase) &&
            !rawXml.Contains("<ScriptText", StringComparison.OrdinalIgnoreCase))
        {
            return templatePath;
        }

        var sanitized = Regex.Replace(
            rawXml,
            @"\sOn[A-Za-z0-9_]+=""[^""]*""",
            string.Empty,
            RegexOptions.IgnoreCase);

        sanitized = Regex.Replace(
            sanitized,
            @"<ScriptText\b[^>]*>.*?</ScriptText>",
            "<ScriptText/>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

        if (string.Equals(rawXml, sanitized, StringComparison.Ordinal))
            return templatePath;

        var tempDir = Path.Combine(Path.GetTempPath(), "SalesMaster", "TemplateSanitized");
        Directory.CreateDirectory(tempDir);

        var sanitizedPath = Path.Combine(
            tempDir,
            $"{Path.GetFileNameWithoutExtension(templatePath)}_scriptless{Path.GetExtension(templatePath)}");
        File.WriteAllText(sanitizedPath, sanitized, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return sanitizedPath;
    }

    private static TemplateMetadata ExtractTemplateMetadata(string templatePath)
    {
        var rawXml = ReadTemplateText(templatePath);
        var decodedXml = WebUtility.HtmlDecode(rawXml);

        var sourceToAlias = ExtractSourceAliasMap(rawXml);
        var fieldSchema = ExtractFieldSchema(rawXml, decodedXml, sourceToAlias);
        EnsureLegacyDataSources(sourceToAlias, fieldSchema);

        var isDualCopyTemplate =
            decodedXml.Contains("mod 2", StringComparison.OrdinalIgnoreCase) ||
            decodedXml.Contains("Line mod 2", StringComparison.OrdinalIgnoreCase) ||
            decodedXml.Contains("공급자 보관용", StringComparison.OrdinalIgnoreCase) &&
            decodedXml.Contains("공급받는자 보관용", StringComparison.OrdinalIgnoreCase);

        return new TemplateMetadata(sourceToAlias, fieldSchema, isDualCopyTemplate);
    }

    private static string ReadTemplateText(string templatePath)
    {
        var bytes = File.ReadAllBytes(templatePath);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Default.GetString(bytes);
        }
    }

    private static Dictionary<string, string> ExtractSourceAliasMap(string rawXml)
    {
        var sourceToAlias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var xdoc = XDocument.Parse(rawXml);
            foreach (var element in xdoc.Descendants())
            {
                var source = element.Attribute("DataSet")?.Value?.Trim();
                var alias = element.Attribute("DataSetName")?.Value?.Trim();
                if (!string.IsNullOrWhiteSpace(source))
                    AddSourceAlias(sourceToAlias, source, alias);

                var elementName = element.Name.LocalName;
                if (elementName.Contains("DBDataset", StringComparison.OrdinalIgnoreCase))
                {
                    var dsSource = element.Attribute("Name")?.Value?.Trim();
                    var dsAlias = element.Attribute("UserName")?.Value?.Trim();
                    if (!string.IsNullOrWhiteSpace(dsSource))
                        AddSourceAlias(sourceToAlias, dsSource, dsAlias);
                }
            }
        }
        catch
        {
            // Ignore XML parse failures and rely on regex discovery below.
        }

        return sourceToAlias;
    }

    private static Dictionary<string, HashSet<string>> ExtractFieldSchema(
        string rawXml,
        string decodedXml,
        Dictionary<string, string> sourceToAlias)
    {
        var schema = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in DataFieldQuotedRegex.Matches(decodedXml))
        {
            var source = match.Groups["dataset"].Value.Trim();
            var field = match.Groups["field"].Value.Trim();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(field))
                continue;

            AddSourceAlias(sourceToAlias, source, alias: null);
            AddField(schema, source, field);
            if (sourceToAlias.TryGetValue(source, out var alias) && !string.IsNullOrWhiteSpace(alias))
                AddField(schema, alias, field);
        }

        foreach (Match match in DataFieldSimpleRegex.Matches(decodedXml))
        {
            var source = match.Groups["dataset"].Value.Trim();
            var field = match.Groups["field"].Value.Trim();
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(field) || field.Contains('"'))
                continue;

            AddSourceAlias(sourceToAlias, source, alias: null);
            AddField(schema, source, field);
            if (sourceToAlias.TryGetValue(source, out var alias) && !string.IsNullOrWhiteSpace(alias))
                AddField(schema, alias, field);
        }

        try
        {
            var xdoc = XDocument.Parse(rawXml);
            foreach (var element in xdoc.Descendants())
            {
                var source = element.Attribute("DataSet")?.Value?.Trim();
                if (string.IsNullOrWhiteSpace(source))
                    continue;

                var alias = element.Attribute("DataSetName")?.Value?.Trim();
                var field = element.Attribute("DataField")?.Value?.Trim();

                AddSourceAlias(sourceToAlias, source, alias);
                if (!string.IsNullOrWhiteSpace(field))
                {
                    AddField(schema, source, field);
                    if (!string.IsNullOrWhiteSpace(alias))
                        AddField(schema, alias, field);
                }
            }
        }
        catch
        {
            // Ignore.
        }

        return schema;
    }

    private static void EnsureLegacyDataSources(
        Dictionary<string, string> sourceToAlias,
        Dictionary<string, HashSet<string>> fieldSchema)
    {
        AddSourceAlias(sourceToAlias, "Top_frxDB", "전표_목록");
        AddSourceAlias(sourceToAlias, "SaeBu_frxDB", "전표_세부");
        AddSourceAlias(sourceToAlias, "Go_frxDBD", "거래처_정보");
        AddSourceAlias(sourceToAlias, "SYSUSER_frxDB", "자사_정보");

        AddSourceAlias(sourceToAlias, "Top", "전표_목록");
        AddSourceAlias(sourceToAlias, "SaeBu", "전표_세부");
        AddSourceAlias(sourceToAlias, "Go", "거래처_정보");
        AddSourceAlias(sourceToAlias, "SYSUSER", "자사_정보");

        foreach (var pair in sourceToAlias.ToArray())
        {
            if (!fieldSchema.ContainsKey(pair.Key))
                fieldSchema[pair.Key] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(pair.Value) && !fieldSchema.ContainsKey(pair.Value))
                fieldSchema[pair.Value] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void AddSourceAlias(Dictionary<string, string> sourceToAlias, string source, string? alias)
    {
        if (string.IsNullOrWhiteSpace(source))
            return;

        source = source.Trim();
        alias = string.IsNullOrWhiteSpace(alias) ? source : alias.Trim();

        if (!sourceToAlias.TryGetValue(source, out var existing) || string.Equals(existing, source, StringComparison.OrdinalIgnoreCase))
        {
            sourceToAlias[source] = alias;
        }

        if (!sourceToAlias.ContainsKey(alias))
            sourceToAlias[alias] = alias;
    }

    private static void AddField(Dictionary<string, HashSet<string>> schema, string dataSetName, string field)
    {
        if (string.IsNullOrWhiteSpace(dataSetName) || string.IsNullOrWhiteSpace(field))
            return;

        if (!schema.TryGetValue(dataSetName, out var set))
        {
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            schema[dataSetName] = set;
        }

        set.Add(field.Trim());
    }

    private static DataSet BuildDataSet(
        TemplateMetadata metadata,
        LocalInvoice invoice,
        LocalCustomer customer,
        LocalCompanyProfile company,
        bool printWithDate,
        bool printWithPrice)
    {
        var result = new DataSet("SalesMaster");
        var detailLines = invoice.Lines.Where(line => !line.IsDeleted).ToList();
        var paidAmount = invoice.Payments.Where(payment => !payment.IsDeleted).Sum(payment => payment.Amount);
        var quantitySum = detailLines.Sum(line => line.Quantity);

        var headerValues = BuildHeaderValues(invoice, paidAmount, quantitySum, printWithDate, printWithPrice);
        var customerValues = BuildCustomerValues(customer);
        var companyValues = BuildCompanyValues(company);

        foreach (var pair in metadata.SourceToAlias)
        {
            var sourceName = pair.Key;
            var aliasName = pair.Value;

            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (metadata.FieldSchema.TryGetValue(sourceName, out var sourceFields))
                fields.UnionWith(sourceFields);
            if (metadata.FieldSchema.TryGetValue(aliasName, out var aliasFields))
                fields.UnionWith(aliasFields);

            var kind = ResolveDataSetKind(sourceName, aliasName, fields);
            var fallbackValues = kind switch
            {
                DataSetKind.Detail => BuildLineValues(line: null, index: 1, printWithPrice),
                DataSetKind.Customer => customerValues,
                DataSetKind.Company => companyValues,
                _ => headerValues
            };

            if (fields.Count == 0)
                fields.UnionWith(fallbackValues.Keys);
            else
                fields.UnionWith(GetEssentialFieldNames(kind));

            if (kind == DataSetKind.Header)
                fields.UnionWith(headerValues.Keys);

            var table = new DataTable(sourceName);
            foreach (var column in fields)
            {
                if (string.IsNullOrWhiteSpace(column) || table.Columns.Contains(column))
                    continue;

                table.Columns.Add(column, typeof(object));
            }

            if (kind == DataSetKind.Detail)
            {
                if (detailLines.Count == 0)
                {
                    var row = table.NewRow();
                    FillRow(row, BuildLineValues(line: null, index: 1, printWithPrice));
                    table.Rows.Add(row);
                }
                else
                {
                    for (var i = 0; i < detailLines.Count; i++)
                    {
                        var row = table.NewRow();
                        FillRow(row, BuildLineValues(detailLines[i], i + 1, printWithPrice));
                        table.Rows.Add(row);
                    }
                }
            }
            else
            {
                var values = kind switch
                {
                    DataSetKind.Customer => customerValues,
                    DataSetKind.Company => companyValues,
                    _ => headerValues
                };

                var rowCount = kind == DataSetKind.Header && metadata.IsDualCopyTemplate ? 2 : 1;
                for (var i = 0; i < rowCount; i++)
                {
                    var row = table.NewRow();
                    FillRow(row, values);
                    table.Rows.Add(row);
                }
            }

            result.Tables.Add(table);
        }

        return result;
    }

    private static IEnumerable<string> GetEssentialFieldNames(DataSetKind kind)
    {
        return kind switch
        {
            DataSetKind.Detail =>
            [
                "순번", "No", "품명", "품목", "규격", "단위", "수량", "단가", "합계", "금액", "공급가", "세액", "비고", "자재번호"
            ],
            DataSetKind.Customer =>
            [
                "상호명/고객", "상호", "사업자번호", "대표자명", "대표전화", "전화번호", "업체주소", "상세주소", "팩스번호", "업태", "종목"
            ],
            DataSetKind.Company =>
            [
                "자사상호", "사업자No", "사업자번호", "대표자명", "전화번호", "사업자주소_1", "사업자주소_2", "자사_비고_1", "자사_비고_2", "자사_도장"
            ],
            _ =>
            [
                "전표번호", "전표날짜", "전표일자", "전표메모", "공급가", "부가세", "합계금액", "전미수금", "누적잔액", "결제액", "수량합계", "페이지순번"
            ]
        };
    }

    private static void RegisterData(Report report, DataSet dataSet, TemplateMetadata metadata)
    {
        foreach (DataTable table in dataSet.Tables)
        {
            report.RegisterData(table, table.TableName);

            var dataSource = report.GetDataSource(table.TableName);
            if (dataSource is null)
                continue;

            dataSource.Enabled = true;
            if (metadata.SourceToAlias.TryGetValue(table.TableName, out var aliasName) &&
                !string.IsNullOrWhiteSpace(aliasName))
            {
                dataSource.Alias = aliasName;
            }
        }
    }

    private static void EnsureDataBandsCanRenderWithoutRows(Report report)
    {
        foreach (var obj in report.AllObjects)
        {
            if (obj is not DataBand dataBand)
                continue;

            dataBand.PrintIfDatasourceEmpty = true;
            if (dataBand.DataSource is null && dataBand.RowCount <= 0)
                dataBand.RowCount = 1;
        }
    }

    private static void FillRow(DataRow row, IReadOnlyDictionary<string, object?> values)
    {
        var normalizedValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in values)
        {
            var normalized = NormalizeKey(pair.Key);
            if (!string.IsNullOrWhiteSpace(normalized) && !normalizedValues.ContainsKey(normalized))
                normalizedValues[normalized] = pair.Value;
        }

        foreach (DataColumn column in row.Table.Columns)
        {
            if (TryResolveColumnValue(column.ColumnName, values, normalizedValues, out var value))
                row[column.ColumnName] = value ?? DBNull.Value;
            else
                row[column.ColumnName] = DBNull.Value;
        }
    }

    private static bool TryResolveColumnValue(
        string columnName,
        IReadOnlyDictionary<string, object?> values,
        IReadOnlyDictionary<string, object?> normalizedValues,
        out object? value)
    {
        if (values.TryGetValue(columnName, out value))
            return true;

        var normalizedColumn = NormalizeKey(columnName);
        if (!string.IsNullOrWhiteSpace(normalizedColumn) &&
            normalizedValues.TryGetValue(normalizedColumn, out value))
        {
            return true;
        }

        var aliases = ResolveColumnAliases(normalizedColumn);
        if (aliases.Count == 0)
        {
            value = null;
            return false;
        }

        foreach (var alias in aliases)
        {
            if (values.TryGetValue(alias, out value))
                return true;

            var normalizedAlias = NormalizeKey(alias);
            if (!string.IsNullOrWhiteSpace(normalizedAlias) &&
                normalizedValues.TryGetValue(normalizedAlias, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static IReadOnlyList<string> ResolveColumnAliases(string normalizedColumn)
    {
        if (string.IsNullOrWhiteSpace(normalizedColumn))
            return Array.Empty<string>();

        if (IsOneOf(normalizedColumn, "합계", "합계금액", "총액", "금액"))
            return ["합계금액", "합계", "금액", "총액"];

        if (IsOneOf(normalizedColumn, "공급가", "공급가액"))
            return ["공급가", "공급가액"];

        if (IsOneOf(normalizedColumn, "부가세", "세액"))
            return ["부가세", "세액"];

        if (IsOneOf(normalizedColumn, "결제액", "수금액", "입금액", "받은금액"))
            return ["결제액", "수금액", "입금액", "받은금액"];

        if (IsOneOf(normalizedColumn, "전미수금", "미수잔액", "누적잔액", "외상미수금", "잔액"))
            return ["전미수금", "미수잔액", "누적잔액", "외상미수금", "잔액"];

        if (IsOneOf(normalizedColumn, "전표날짜", "전표일자", "일자", "작성일자"))
            return ["전표날짜", "전표일자", "일자", "작성일자"];

        if (IsOneOf(normalizedColumn, "상호명고객", "상호", "거래처명", "고객명", "고객거래처"))
            return ["상호명/고객", "상호", "거래처명", "고객명", "고객/거래처"];

        if (IsOneOf(normalizedColumn, "대표전화", "전화번호", "연락처", "전화"))
            return ["대표전화", "전화번호", "연락처", "전화"];

        if (IsOneOf(normalizedColumn, "업체주소", "주소"))
            return ["업체주소", "주소", "사업자주소_1", "사업자주소_2"];

        if (IsOneOf(normalizedColumn, "대표자명", "대표자"))
            return ["대표자명", "대표자"];

        if (IsOneOf(normalizedColumn, "품명", "품목", "제품명"))
            return ["품명", "품목", "제품명"];

        if (IsOneOf(normalizedColumn, "순번", "NO", "번호"))
            return ["순번", "No", "번호"];

        return Array.Empty<string>();
    }

    private static bool IsOneOf(string value, params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (string.Equals(value, NormalizeKey(candidate), StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string NormalizeKey(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch) || (ch >= '가' && ch <= '힣'))
                builder.Append(char.ToUpperInvariant(ch));
        }

        return builder.ToString();
    }

    private enum DataSetKind
    {
        Header = 0,
        Customer = 1,
        Company = 2,
        Detail = 3
    }

    private static DataSetKind ResolveDataSetKind(
        string sourceName,
        string aliasName,
        IReadOnlySet<string> fields)
    {
        var source = sourceName.ToLowerInvariant();
        var alias = aliasName.ToLowerInvariant();

        if (ContainsAny(source, alias, "saebu", "detail", "line", "세부", "품목", "item"))
            return DataSetKind.Detail;

        if (ContainsAny(source, alias, "go_", "gofrxdb", "customer", "거래처", "고객"))
            return DataSetKind.Customer;

        if (ContainsAny(source, alias, "sysuser", "supplier", "자사", "회사"))
            return DataSetKind.Company;

        if (ContainsAny(source, alias, "top", "목록", "전표", "title"))
            return DataSetKind.Header;

        if (ContainsField(fields, "품명") || ContainsField(fields, "품목") || ContainsField(fields, "수량"))
            return DataSetKind.Detail;

        if (ContainsField(fields, "자사상호") || ContainsField(fields, "사업자주소_1") || ContainsField(fields, "자사_비고_1"))
            return DataSetKind.Company;

        if (ContainsField(fields, "상호명/고객") || ContainsField(fields, "업체주소") || ContainsField(fields, "대표전화"))
            return DataSetKind.Customer;

        return DataSetKind.Header;
    }

    private static bool ContainsAny(string source, string alias, params string[] tokens)
    {
        foreach (var token in tokens)
        {
            if (source.Contains(token, StringComparison.OrdinalIgnoreCase) ||
                alias.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsField(IReadOnlySet<string> fields, string key)
    {
        var normalizedKey = NormalizeKey(key);
        foreach (var field in fields)
        {
            if (string.Equals(NormalizeKey(field), normalizedKey, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private sealed record TemplateMetadata(
        IReadOnlyDictionary<string, string> SourceToAlias,
        IReadOnlyDictionary<string, HashSet<string>> FieldSchema,
        bool IsDualCopyTemplate);

    private static Dictionary<string, object?> BuildHeaderValues(
        LocalInvoice invoice,
        decimal paidAmount,
        decimal quantitySum,
        bool printWithDate,
        bool printWithPrice)
    {
        var total = invoice.TotalAmount;
        var supply = invoice.SupplyAmount;
        var vat = invoice.VatAmount;
        var balance = total - paidAmount;

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["전표번호"] = string.IsNullOrWhiteSpace(invoice.InvoiceNumber) ? invoice.LocalTempNumber : invoice.InvoiceNumber,
            ["전표날짜"] = printWithDate ? invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
            ["전표일자"] = printWithDate ? invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
            ["일자"] = printWithDate ? invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
            ["작성일자"] = printWithDate ? invoice.InvoiceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : string.Empty,
            ["전표메모"] = invoice.Memo ?? string.Empty,

            ["공급가"] = printWithPrice ? supply : 0m,
            ["공급가액"] = printWithPrice ? supply : 0m,
            ["부가세"] = printWithPrice ? vat : 0m,
            ["세액"] = printWithPrice ? vat : 0m,
            ["합계"] = printWithPrice ? total : 0m,
            ["합계금액"] = printWithPrice ? total : 0m,
            ["총액"] = printWithPrice ? total : 0m,
            ["금액"] = printWithPrice ? total : 0m,

            ["결제액"] = printWithPrice ? paidAmount : 0m,
            ["수금액"] = printWithPrice ? paidAmount : 0m,
            ["입금액"] = printWithPrice ? paidAmount : 0m,
            ["받은금액"] = printWithPrice ? paidAmount : 0m,

            ["전미수금"] = printWithPrice ? balance : 0m,
            ["누적잔액"] = printWithPrice ? balance : 0m,
            ["미수잔액"] = printWithPrice ? balance : 0m,
            ["외상미수금"] = printWithPrice ? balance : 0m,
            ["잔액"] = printWithPrice ? balance : 0m,

            ["수량합계"] = quantitySum,
            ["페이지순번"] = 1,
            ["출력일자시간"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["\uCD9C\uB825\uC77C\uC790\uC2DC\uAC04"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            ["\uB85C\uACE0\uC778"] = string.Empty,
            ["인수자"] = string.Empty,
            ["DC액"] = 0m,
            ["누적수금"] = printWithPrice ? paidAmount : 0m
        };
    }

    private static Dictionary<string, object?> BuildCustomerValues(LocalCustomer customer)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["상호명/고객"] = customer.NameOriginal ?? string.Empty,
            ["상호"] = customer.NameOriginal ?? string.Empty,
            ["고객명"] = customer.NameOriginal ?? string.Empty,
            ["거래처명"] = customer.NameOriginal ?? string.Empty,
            ["고객/거래처"] = customer.NameOriginal ?? string.Empty,

            ["사업자번호"] = customer.BusinessNumber ?? string.Empty,
            ["대표자명"] = customer.Representative ?? string.Empty,
            ["대표전화"] = customer.Phone ?? string.Empty,
            ["전화번호"] = customer.Phone ?? string.Empty,
            ["연락처"] = customer.Phone ?? string.Empty,
            ["휴대폰"] = customer.MobilePhone ?? string.Empty,
            ["팩스번호"] = customer.FaxNumber ?? string.Empty,

            ["업체주소"] = customer.Address ?? string.Empty,
            ["상세주소"] = customer.DetailAddress ?? string.Empty,
            ["주소"] = customer.Address ?? string.Empty,
            ["담당자"] = customer.ContactPerson ?? string.Empty,
            ["부서"] = customer.Department ?? string.Empty,
            ["업태"] = customer.BusinessType ?? string.Empty,
            ["종목"] = customer.BusinessItem ?? string.Empty,
            ["수신자"] = customer.Recipient ?? string.Empty,
            ["이메일"] = customer.Email ?? string.Empty
        };
    }

    private static Dictionary<string, object?> BuildCompanyValues(LocalCompanyProfile company)
    {
        var address1 = company.Address ?? string.Empty;
        var address2 = string.Empty;

        if (!string.IsNullOrWhiteSpace(company.Address))
        {
            var parts = company.Address
                .Split([' ', ',', '/'], StringSplitOptions.RemoveEmptyEntries)
                .ToList();
            if (parts.Count > 4)
            {
                address1 = string.Join(' ', parts.Take(4));
                address2 = string.Join(' ', parts.Skip(4));
            }
        }

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["자사상호"] = company.TradeName ?? string.Empty,
            ["상호"] = company.TradeName ?? string.Empty,
            ["사업자No"] = company.BusinessNumber ?? string.Empty,
            ["사업자번호"] = company.BusinessNumber ?? string.Empty,
            ["대표자명"] = company.Representative ?? string.Empty,
            ["전화번호"] = company.ContactNumber ?? string.Empty,
            ["연락처"] = company.ContactNumber ?? string.Empty,
            ["사업자주소_1"] = address1,
            ["사업자주소_2"] = address2,
            ["주소"] = company.Address ?? string.Empty,
            ["업태"] = company.BusinessType ?? string.Empty,
            ["종목"] = company.BusinessItem ?? string.Empty,
            ["자사_비고_1"] = company.BankAccountText ?? string.Empty,
            ["자사_비고_2"] = company.BankAccountText ?? string.Empty,
            ["자사_도장"] = company.StampImage,
            ["이메일"] = company.Email ?? string.Empty
        };
    }

    private static Dictionary<string, object?> BuildLineValues(LocalInvoiceLine? line, int index, bool printWithPrice)
    {
        if (line is null)
        {
            return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["순번"] = index,
                ["No"] = index,
                ["번호"] = index,
                ["품명"] = string.Empty,
                ["품목"] = string.Empty,
                ["제품명"] = string.Empty,
                ["규격"] = string.Empty,
                ["단위"] = string.Empty,
                ["수량"] = 0m,
                ["단가"] = 0m,
                ["공급가"] = 0m,
                ["세액"] = 0m,
                ["합계"] = 0m,
                ["금액"] = 0m,
                ["비고"] = string.Empty,
                ["자재번호"] = string.Empty
            };
        }

        var lineTotal = printWithPrice ? line.LineAmount : 0m;
        var lineSupply = printWithPrice
            ? Math.Round(line.LineAmount / 1.1m, 0, MidpointRounding.AwayFromZero)
            : 0m;
        var lineVat = printWithPrice ? line.LineAmount - lineSupply : 0m;

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["순번"] = index,
            ["No"] = index,
            ["번호"] = index,
            ["품명"] = line.ItemNameOriginal ?? string.Empty,
            ["품목"] = line.ItemNameOriginal ?? string.Empty,
            ["제품명"] = line.ItemNameOriginal ?? string.Empty,
            ["규격"] = line.SpecificationOriginal ?? string.Empty,
            ["단위"] = line.Unit ?? string.Empty,
            ["수량"] = line.Quantity,
            ["단가"] = printWithPrice ? line.UnitPrice : 0m,
            ["공급가"] = lineSupply,
            ["세액"] = lineVat,
            ["합계"] = lineTotal,
            ["금액"] = lineTotal,
            ["비고"] = line.Remark ?? string.Empty,
            ["자재번호"] = line.MaterialNumber ?? string.Empty
        };
    }
}
