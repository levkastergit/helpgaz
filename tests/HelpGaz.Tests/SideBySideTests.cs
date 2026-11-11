using System.IO.Compression;
using System.Text;
using HelpGaz.Core.Compare;
using HelpGaz.Core.Docx;
using Xunit;

namespace HelpGaz.Tests;

/// <summary>Наглядное сравнение. Документы вымышленные, собираются здесь же.</summary>
public class SideBySideTests
{
    private const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";

    private static string P(string text, string rPr = "") =>
        $"<w:p><w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static DocModel Doc(params string[] paragraphs)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open(), new UTF8Encoding(false));
            w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Ns}><w:body>{string.Concat(paragraphs)}"
                + "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"850\" w:bottom=\"1134\" w:left=\"1701\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>"
                + "</w:body></w:document>");
        }
        return DocxReader.Read(ms.ToArray());
    }

    private static VisualComparison Build(DocModel template, DocModel file, FindingKind kind, string where, string expected = "", string actual = "") =>
        SideBySide.Build(TemplateModel.Parse(template), file, kind, where, expected, actual);

    private static string Marked(VisualPara? p, VisualMark mark) =>
        string.Concat(p!.Runs.Where(r => r.Mark == mark).Select(r => r.Text));

    [Fact]
    public void ChangedTextShowsRemovedAddedAndValue()
    {
        var cmp = Build(Doc(P("Шапка"), P("Акт составлен по адресу {address} комиссией")),
            Doc(P("Шапка"), P("Акт оформлен по адресу Энск комиссией")),
            FindingKind.TemplateText, "абзац 2");

        var row = Assert.Single(cmp.Rows, r => r?.Focus == true)!;
        Assert.Equal("составлен", Marked(row.Left, VisualMark.Removed));
        Assert.Equal("{address}", Marked(row.Left, VisualMark.Tag));
        Assert.Equal("оформлен", Marked(row.Right, VisualMark.Added));
        Assert.Equal("Энск", Marked(row.Right, VisualMark.Value));
    }

    [Fact]
    public void MissingParagraphHasEmptyFileSide()
    {
        var cmp = Build(Doc(P("Первый абзац шаблона"), P("Удаляемый абзац {knr}"), P("Последний абзац шаблона")),
            Doc(P("Первый абзац шаблона"), P("Последний абзац шаблона")),
            FindingKind.ParagraphMissing, "абзац 2", expected: "Удаляемый абзац {knr}");

        var row = Assert.Single(cmp.Rows, r => r?.Focus == true)!;
        Assert.Equal(RowKind.Missing, row.Kind);
        Assert.Null(row.Right);
        Assert.Equal(3, cmp.Rows.Count);
    }

    [Fact]
    public void FontChangeIsMarkedOnBothSides()
    {
        var cmp = Build(Doc(P("Свод правил 62", "<w:rFonts w:ascii=\"Times New Roman\" w:hAnsi=\"Times New Roman\"/>")),
            Doc(P("Свод правил 62", "<w:rFonts w:ascii=\"Arial\" w:hAnsi=\"Arial\"/>")),
            FindingKind.CharFormatting, "абзац 1");

        var row = Assert.Single(cmp.Rows, r => r?.Focus == true)!;
        Assert.Equal("Свод правил 62".Replace(" ", ""), Marked(row.Right, VisualMark.Format).Replace(" ", ""));
        Assert.Contains(row.Left!.Runs, r => r.Format?.Font == "Times New Roman");
        Assert.Contains(row.Right!.Runs, r => r.Format?.Font == "Arial");

        var html = ComparisonHtml.Render("Оформление текста", "шаблон.docx", Array.Empty<(string, string)>(), "файл.docx", cmp, null);
        Assert.Contains("абзац 1 · Times New Roman", html);
        Assert.Contains("абзац 1 · Arial", html);
    }

    [Fact]
    public void BlankLinesAreFocused()
    {
        var cmp = Build(Doc(P("Первый"), P("Второй")), Doc(P("Первый"), P(""), P(""), P("Второй")), FindingKind.BlankLines, "");
        Assert.Equal(2, cmp.Rows.Count(r => r?.Focus == true && r.Kind == RowKind.Added));
    }

    [Fact]
    public void FarPlacesAreSeparatedByGap()
    {
        var body = Enumerable.Range(1, 20).Select(i => P($"Абзац номер {i}")).ToArray();
        var file = body.ToArray();
        file[14] = P("Абзац номер пятнадцать");
        var cmp = Build(Doc(body), Doc(file), FindingKind.TemplateText, "абзац 15");
        Assert.Null(cmp.Rows[0]);
        Assert.Equal(7, cmp.Rows.Count(r => r is not null));
        Assert.Null(cmp.Rows[^1]);
    }

    [Fact]
    public void UnknownPlaceGivesNote()
    {
        var cmp = Build(Doc(P("Текст")), Doc(P("Текст")), FindingKind.TemplateText, "абзац 9");
        Assert.Empty(cmp.Rows);
        Assert.Contains("не найдено", cmp.Note);
    }

    [Fact]
    public void HtmlEscapesDocumentText()
    {
        var cmp = Build(Doc(P("Давление испытания &lt;b&gt; не менее 1 МПа")), Doc(P("Давление испытания &lt;b&gt; не менее 2 МПа")),
            FindingKind.TemplateText, "абзац 1");
        Assert.Contains(cmp.Rows, r => r?.Focus == true);
        var html = ComparisonHtml.Render("Изменён текст", "шаблон.docx", new[] { ("Суть:", "a < b") }, "файл.docx", cmp, null);
        Assert.Contains("&lt;b&gt;", html);
        Assert.DoesNotContain("<b> не", html);
        Assert.Contains("Шаблон на сайте", html);
        Assert.Contains("a &lt; b", html);
    }
}
