using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace HelpGaz.Core.Docx;

/// <summary>Читает .docx в плоский список абзацев с посимвольным оформлением.</summary>
public static class DocxReader
{
    internal static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace Mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
    private static readonly XNamespace Rel = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace Xml = XNamespace.Xml;

    public static DocModel Read(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        return Read(ms);
    }

    public static DocModel Read(Stream stream)
    {
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var doc = LoadPart(zip, "word/document.xml")
            ?? throw new InvalidDataException("В файле нет word/document.xml — это не документ Word.");
        var walker = new Walker(new StyleResolver(LoadPart(zip, "word/styles.xml")));

        var body = doc.Root?.Element(W + "body") ?? throw new InvalidDataException("В документе нет тела.");
        walker.WalkBlocks(body, "");

        var sections = new List<SectionLayout>();
        var sectPrs = body.Descendants(W + "sectPr").ToList();
        foreach (var s in sectPrs) sections.Add(ReadSection(s));

        ReadHeadersFooters(zip, sectPrs, walker);

        return new DocModel { Paragraphs = walker.Result, Sections = sections };
    }

    private static XDocument? LoadPart(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var s = entry.Open();
        return XDocument.Load(s, LoadOptions.PreserveWhitespace);
    }

    private static SectionLayout ReadSection(XElement s)
    {
        var sz = s.Element(W + "pgSz");
        var m = s.Element(W + "pgMar");
        return new SectionLayout(
            Int(sz, "w"), Int(sz, "h"), (string?)sz?.Attribute(W + "orient") ?? "portrait",
            Int(m, "top"), Int(m, "right"), Int(m, "bottom"), Int(m, "left"),
            Int(m, "header"), Int(m, "footer"), Int(m, "gutter"));
    }

    private static int Int(XElement? e, string attr) =>
        int.TryParse((string?)e?.Attribute(W + attr), out var v) ? v : 0;

    private static void ReadHeadersFooters(ZipArchive zip, List<XElement> sectPrs, Walker walker)
    {
        var rels = LoadPart(zip, "word/_rels/document.xml.rels");
        if (rels?.Root is null) return;
        var targets = rels.Root.Elements(Rel + "Relationship")
            .Where(r => r.Attribute("Id") is not null && r.Attribute("Target") is not null)
            .ToDictionary(r => (string)r.Attribute("Id")!, r => (string)r.Attribute("Target")!);

        var seen = new HashSet<string>();
        foreach (var kind in new[] { "header", "footer" })
        foreach (var type in new[] { "default", "first", "even" })
        foreach (var s in sectPrs)
        foreach (var refEl in s.Elements(W + kind + "Reference").Where(e => (string?)e.Attribute(W + "type") == type))
        {
            var id = (string?)refEl.Attribute(R + "id");
            if (id is null || !targets.TryGetValue(id, out var target) || !seen.Add(target)) continue;
            var part = LoadPart(zip, "word/" + target.TrimStart('/').Replace("word/", ""));
            if (part?.Root is null) continue;
            var label = (kind == "header" ? "Верхний колонтитул" : "Нижний колонтитул")
                + type switch { "first" => " (первая страница)", "even" => " (чётные)", _ => "" };
            walker.WalkBlocks(part.Root, label + ", ");
        }
    }

    private sealed class Walker(StyleResolver styles)
    {
        public readonly List<DocPara> Result = new();
        private int _bodyParagraph;
        private int _table;
        private int _rowId;
        private int _cellId;

        public void WalkBlocks(XElement container, string prefix)
        {
            foreach (var el in container.Elements())
            {
                if (el.Name == W + "p") Paragraph(el, prefix + "абзац " + ++_bodyParagraph, prefix + "body");
                else if (el.Name == W + "tbl") Table(el, prefix);
                else if (el.Name == W + "sdt") { var c = el.Element(W + "sdtContent"); if (c is not null) WalkBlocks(c, prefix); }
                else if (el.Name == W + "customXml") WalkBlocks(el, prefix);
            }
        }

        private void Table(XElement tbl, string prefix)
        {
            var number = ++_table;
            var row = 0;
            foreach (var tr in ChildrenThroughSdt(tbl, "tr"))
            {
                row++;
                var rowId = ++_rowId;
                var col = 0;
                foreach (var tc in ChildrenThroughSdt(tr, "tc"))
                {
                    col++;
                    var where = $"{prefix}таблица {number}, строка {row}, столбец {col}";
                    var container = $"{prefix}table:{col}";
                    var cellId = ++_cellId;
                    foreach (var el in tc.Elements())
                    {
                        if (el.Name == W + "p") Paragraph(el, where, container, rowId, cellId);
                        else if (el.Name == W + "tbl") Table(el, where + ", ");
                        else if (el.Name == W + "sdt") { var c = el.Element(W + "sdtContent"); if (c is not null) WalkCell(c, where, container, rowId, cellId); }
                    }
                }
            }
        }

        private void WalkCell(XElement content, string where, string container, int rowId, int cellId)
        {
            foreach (var el in content.Elements())
            {
                if (el.Name == W + "p") Paragraph(el, where, container, rowId, cellId);
                else if (el.Name == W + "tbl") Table(el, where + ", ");
            }
        }

        private static IEnumerable<XElement> ChildrenThroughSdt(XElement parent, string local)
        {
            foreach (var el in parent.Elements())
            {
                if (el.Name == W + local) yield return el;
                else if (el.Name == W + "sdt")
                {
                    var content = el.Element(W + "sdtContent");
                    if (content is null) continue;
                    foreach (var inner in ChildrenThroughSdt(content, local)) yield return inner;
                }
            }
        }

        private sealed class OpenField
        {
            public readonly StringBuilder Instruction = new();
            public bool InResult;
            public int ResultStart;
        }

        private sealed class ParaBuilder
        {
            public readonly StringBuilder Text = new();
            public readonly List<CharFormat> Formats = new();
            public readonly List<FieldSpan> Fields = new();
            public readonly Stack<OpenField> OpenFields = new();
            public readonly List<XElement> TextBoxes = new();
            public bool PageBreak;

            public void Append(string s, CharFormat f)
            {
                Text.Append(s);
                for (var i = 0; i < s.Length; i++) Formats.Add(f);
            }
        }

        private void Paragraph(XElement p, string location, string container, int rowId = -1, int cellId = -1)
        {
            var pPr = p.Element(W + "pPr");
            var styleId = styles.ParaStyleId(pPr);
            var b = new ParaBuilder { PageBreak = pPr?.Element(W + "pageBreakBefore") is { } pb && (string?)pb.Attribute(W + "val") is not ("0" or "false") };

            Inline(p, styleId, b);

            while (b.OpenFields.Count > 0) CloseField(b);

            Result.Add(new DocPara
            {
                Index = Result.Count,
                Location = location,
                Container = container,
                RowId = rowId,
                CellId = cellId,
                Text = b.Text.ToString(),
                Formats = b.Formats.ToArray(),
                Format = styles.Paragraph(styleId, pPr),
                HasPageBreak = b.PageBreak,
                Fields = b.Fields,
            });

            foreach (var box in b.TextBoxes) WalkTextBox(box, location);
        }

        private void WalkTextBox(XElement box, string location)
        {
            foreach (var el in box.Elements())
            {
                if (el.Name == W + "p") Paragraph(el, location + " (надпись)", "textbox");
                else if (el.Name == W + "tbl") Table(el, location + " (надпись), ");
            }
        }

        private void Inline(XElement parent, string? styleId, ParaBuilder b)
        {
            foreach (var el in parent.Elements())
            {
                if (el.Name == Mc + "AlternateContent")
                {
                    var branch = el.Element(Mc + "Choice") ?? el.Element(Mc + "Fallback");
                    if (branch is not null) Inline(branch, styleId, b);
                    continue;
                }
                if (el.Name.Namespace != W) continue;

                switch (el.Name.LocalName)
                {
                    case "r":
                        Run(el, styleId, b);
                        break;
                    case "hyperlink" or "smartTag" or "ins" or "moveTo" or "customXml" or "sdtContent":
                        Inline(el, styleId, b);
                        break;
                    case "sdt":
                        var content = el.Element(W + "sdtContent");
                        if (content is not null) Inline(content, styleId, b);
                        break;
                    case "fldSimple":
                        var start = b.Text.Length;
                        Inline(el, styleId, b);
                        b.Fields.Add(new FieldSpan(start, b.Text.Length - start, (string?)el.Attribute(W + "instr") ?? ""));
                        break;
                }
            }
        }

        private void Run(XElement r, string? styleId, ParaBuilder b)
        {
            var format = styles.Run(styleId, r.Element(W + "rPr"));
            foreach (var el in r.Elements())
            {
                if (el.Name == Mc + "AlternateContent")
                {
                    var branch = el.Element(Mc + "Choice") ?? el.Element(Mc + "Fallback");
                    if (branch is not null) b.TextBoxes.AddRange(TopLevelTextBoxes(branch));
                    continue;
                }
                if (el.Name.Namespace != W) continue;

                switch (el.Name.LocalName)
                {
                    case "t":
                        var preserve = (string?)el.Attribute(Xml + "space") == "preserve";
                        b.Append(preserve ? el.Value : el.Value.Trim(), format);
                        break;
                    case "tab":
                        b.Append("\t", format);
                        break;
                    case "br":
                        var type = (string?)el.Attribute(W + "type");
                        if (type == "page") b.PageBreak = true;
                        else if (type != "column") b.Append("\n", format);
                        break;
                    case "cr":
                        b.Append("\n", format);
                        break;
                    case "noBreakHyphen":
                        b.Append("‑", format);
                        break;
                    case "sym":
                        var hex = (string?)el.Attribute(W + "char");
                        if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var code))
                            b.Append(char.ConvertFromUtf32(code >= 0xF000 ? code - 0xF000 : code), format);
                        break;
                    case "fldChar":
                        FieldChar((string?)el.Attribute(W + "fldCharType"), b);
                        break;
                    case "instrText":
                        if (b.OpenFields.TryPeek(out var field) && !field.InResult) field.Instruction.Append(el.Value);
                        break;
                    case "drawing" or "pict" or "object":
                        b.TextBoxes.AddRange(TopLevelTextBoxes(el));
                        break;
                }
            }
        }

        private static IEnumerable<XElement> TopLevelTextBoxes(XElement root) =>
            root.Descendants(W + "txbxContent")
                .Where(t => !t.Ancestors(W + "txbxContent").Any(a => root.Descendants().Contains(a))
                         && !t.Ancestors(Mc + "Fallback").Any(a => root.Descendants().Contains(a)));

        private static void FieldChar(string? type, ParaBuilder b)
        {
            switch (type)
            {
                case "begin":
                    b.OpenFields.Push(new OpenField());
                    break;
                case "separate":
                    if (b.OpenFields.TryPeek(out var f)) { f.InResult = true; f.ResultStart = b.Text.Length; }
                    break;
                case "end":
                    if (b.OpenFields.Count > 0) CloseField(b);
                    break;
            }
        }

        private static void CloseField(ParaBuilder b)
        {
            var f = b.OpenFields.Pop();
            if (f.InResult) b.Fields.Add(new FieldSpan(f.ResultStart, b.Text.Length - f.ResultStart, f.Instruction.ToString()));
        }
    }
}
