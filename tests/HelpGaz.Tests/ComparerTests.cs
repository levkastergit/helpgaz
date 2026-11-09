using System.IO.Compression;
using System.Text;
using HelpGaz.Core.Compare;
using HelpGaz.Core.Docx;
using HelpGaz.Core.Log;
using HelpGaz.Core.Site;
using Xunit;

namespace HelpGaz.Tests;

/// <summary>Все документы собираются здесь же из вымышленного текста: реальных шаблонов в репозитории нет.</summary>
public class ComparerTests
{
    private const string Ns = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"";
    private const string Sect = "<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"850\" w:bottom=\"1134\" w:left=\"1701\" w:header=\"708\" w:footer=\"708\" w:gutter=\"0\"/></w:sectPr>";

    private static string P(string text, string rPr = "", string pPr = "") =>
        $"<w:p>{(pPr.Length > 0 ? $"<w:pPr>{pPr}</w:pPr>" : "")}<w:r>{(rPr.Length > 0 ? $"<w:rPr>{rPr}</w:rPr>" : "")}<w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";

    private static string Row(params string[] cells) =>
        "<w:tr>" + string.Concat(cells.Select(c => $"<w:tc>{P(c)}</w:tc>")) + "</w:tr>";

    private static string Table(params string[] rows) => "<w:tbl>" + string.Concat(rows) + "</w:tbl>";

    private static DocModel Doc(string body, string sect = Sect)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:document {Ns}><w:body>{body}{sect}</w:body></w:document>");
        }
        return DocxReader.Read(ms.ToArray());
    }

    private static DocumentResult Compare(string template, string file, string? site = null, string sect = Sect) =>
        DocumentComparer.Compare(new CompareInput
        {
            FileName = "файл.docx",
            TemplateName = "шаблон.docx",
            File = Doc(file, sect),
            Template = TemplateModel.Parse(Doc(template)),
            SiteDocument = site is null ? null : Doc(site),
        });

    private static List<Finding> Real(DocumentResult r) => r.Findings.Where(f => f.Kind != FindingKind.FrozenField).ToList();

    [Fact]
    public void FilledCopyHasNoFindings()
    {
        var r = Compare(P("Адрес: {address}, заказчик {fio}"), P("Адрес: г. Энск, ул. Тестовая, 1, заказчик Петров П.П."));
        Assert.Empty(Real(r));
    }

    [Fact]
    public void ChangedTemplateTextIsReportedWithTags()
    {
        var r = Compare(P("Акт составлен по адресу {address} комиссией"), P("Акт оформлен по адресу Энск комиссией"));
        var f = Assert.Single(Real(r));
        Assert.Equal(FindingKind.TemplateText, f.Kind);
        Assert.Equal("Акт составлен по адресу {address} комиссией", f.Expected);
        Assert.Equal("Акт оформлен по адресу {address} комиссией", f.Actual);
    }

    [Fact]
    public void SameEditInDifferentFoldersGroupsTogether()
    {
        var a = Real(Compare(P("Акт составлен по адресу {address} комиссией"), P("Акт оформлен по адресу Энск комиссией")))[0];
        var b = Real(Compare(P("Акт составлен по адресу {address} комиссией"), P("Акт оформлен по адресу Заречный комиссией")))[0];
        Assert.Equal(a.GroupKey, b.GroupKey);
    }

    [Fact]
    public void ValueDifferentFromSiteIsReported()
    {
        var r = Compare(P("Заказчик: {fio}"), P("Заказчик: Сидоров"), P("Заказчик: Петров"));
        var f = Assert.Single(Real(r));
        Assert.Equal(FindingKind.ValueMismatch, f.Kind);
        Assert.Equal("fio", f.Tag);
        Assert.Equal("Петров", f.Expected);
        Assert.Equal("Сидоров", f.Actual);
    }

    [Fact]
    public void ExpiredCertificateMarkIsIgnored()
    {
        var r = Compare(P("Сертификат: {cert}"), P("Сертификат: С-1"), P("Сертификат: С-1 (истёк)"));
        Assert.Empty(Real(r));
    }

    [Fact]
    public void ParagraphLoopRepeatsWholeParagraph()
    {
        var tpl = P("Заголовок") + P("{#items}") + P("Пункт {name}") + P("{/items}") + P("Конец");
        var file = P("Заголовок") + P("Пункт один") + P("Пункт два") + P("Конец");
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void LoopOnSingleParagraphRepeats()
    {
        var tpl = P("{#items}{name} — готово{/items}") + P("Конец");
        var file = P("Один — готово") + P("Два — готово") + P("Конец");
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void RowLoopRepeatsCellsOutsideTags()
    {
        var tpl = Table(Row("№", "Имя", "Итог"), Row("", "{#rows}{name}", "{value}{/rows}"), Row("Всего", "", ""));
        var file = Table(Row("№", "Имя", "Итог"), Row("", "А", "1"), Row("", "Б", "2"), Row("Всего", "", ""));
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void MarkerNextToContentLeavesBlankLine()
    {
        var tpl = P("{#rows}{name}") + P("{/rows}") + P("Конец");
        var file = P("А") + P("Б") + P("") + P("Конец");
        Assert.Empty(Real(Compare(tpl, file)));
    }

    [Fact]
    public void RowCountDifferenceIsReported()
    {
        var tpl = P("{#items}") + P("Пункт {name}") + P("{/items}");
        var file = P("Пункт один");
        var site = P("Пункт один") + P("Пункт два");
        var f = Assert.Single(Real(Compare(tpl, file, site)));
        Assert.Equal(FindingKind.RowCount, f.Kind);
    }

    [Fact]
    public void BoldOnLiteralIsReported()
    {
        var f = Assert.Single(Real(Compare(P("Постоянный текст"), P("Постоянный текст", "<w:b/>"))));
        Assert.Equal(FindingKind.CharFormatting, f.Kind);
        Assert.Contains("Жирность", f.Summary);
    }

    [Fact]
    public void AlignmentChangeIsReported()
    {
        var f = Assert.Single(Real(Compare(P("Текст"), P("Текст", "", "<w:jc w:val=\"center\"/>"))));
        Assert.Equal(FindingKind.ParaFormatting, f.Kind);
    }

    [Fact]
    public void MarginChangeIsReported()
    {
        var changed = Sect.Replace("w:left=\"1701\"", "w:left=\"567\"");
        var f = Assert.Single(Real(Compare(P("Текст"), P("Текст"), sect: changed)));
        Assert.Equal(FindingKind.PageLayout, f.Kind);
        Assert.Contains("Поле слева", f.Summary);
    }

    [Fact]
    public void AddedAndRemovedParagraphsAreReported()
    {
        var r = Real(Compare(P("Первый абзац текста") + P("Второй абзац текста"), P("Первый абзац текста") + P("Совсем новое дополнение")));
        Assert.Contains(r, f => f.Kind == FindingKind.ParagraphAdded);
        Assert.Contains(r, f => f.Kind == FindingKind.ParagraphMissing);
    }

    [Fact]
    public void LeftoverTagIsReported()
    {
        var r = Real(Compare(P("Адрес: {address}"), P("Адрес: {address}")));
        Assert.Contains(r, f => f.Kind == FindingKind.Unfilled);
    }

    [Fact]
    public void EmptyValueIsReported()
    {
        var f = Assert.Single(Real(Compare(P("Адрес: {address}"), P("Адрес: "))));
        Assert.Equal(FindingKind.EmptyField, f.Kind);
    }

    [Fact]
    public void WrappedLiteralInSplitRunsStillMatches()
    {
        var split = "<w:p><w:r><w:t>Постоян</w:t></w:r><w:r><w:t xml:space=\"preserve\">ный </w:t></w:r><w:r><w:t>текст</w:t></w:r></w:p>";
        Assert.Empty(Real(Compare(P("Постоянный текст"), split)));
    }

    [Fact]
    public void FolderConflictIsFoundWhenSiteValuesAgree()
    {
        var d1 = Compare(P("Адрес: {address}"), P("Адрес: Энск"), P("Адрес: Энск"));
        var d2 = Compare(P("Объект: {address}"), P("Объект: Энск"), P("Объект: Энск"));
        var d3 = Compare(P("Место: {address}"), P("Место: Заречный"), P("Место: Энск"));
        var f = Assert.Single(FolderChecks.CrossCheck(new[] { d1, d2, d3 }));
        Assert.Equal(FindingKind.FolderConflict, f.Kind);
        Assert.Equal("Заречный", f.Actual);
    }

    [Fact]
    public void LegitimatelyDifferentDatesAreNotConflict()
    {
        var d1 = Compare(P("Дата: {date}"), P("Дата: 01.02.2026"), P("Дата: 01.02.2026"));
        var d2 = Compare(P("Дата: {date}"), P("Дата: 05.03.2026"), P("Дата: 05.03.2026"));
        Assert.Empty(FolderChecks.CrossCheck(new[] { d1, d2 }));
    }

    [Fact]
    public void BrowserCopySuffixDoesNotChangeHash()
    {
        Assert.Equal(DocumentCatalog.Hash("13 Акт.docx"), DocumentCatalog.Hash("13  акт (1).docx"));
    }

    [Theory]
    [InlineData("№ 77-А Петров", true)]
    [InlineData("Сидоров", false)]
    public void ContractIsChosenOnlyWithStrongEvidence(string folder, bool chosen)
    {
        var contracts = new[]
        {
            new Contract(1, "77-А", "г. Энск, ул. Тестовая, д. 5", "Петров Пётр Петрович", ""),
            new Contract(2, "78", "г. Энск, ул. Лесная, д. 9", "Сидоров Иван", ""),
            new Contract(3, "79", "г. Энск, ул. Речная, д. 3", "Сидоров Олег", ""),
        };
        var match = ContractMatcher.Match(folder, contracts);
        Assert.Equal(chosen, match.Contract is not null);
    }

    [Fact]
    public void RescanDoesNotDuplicateAndKeepsNormalStatus()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new FindingsStore(path);
            var finding = Real(Compare(P("Текст шаблона"), P("Текст файла")))[0];
            var record = new FolderRecord { Path = "C:\\A" };

            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            var group = Assert.Single(store.Groups());
            Assert.Single(group.Occurrences);

            store.SetStatus(group.Key, FindingStatus.Normal);
            store.ReplaceFolderResults(record, Array.Empty<Finding>(), DateTime.Now);
            Assert.Equal(FindingStatus.Normal, Assert.Single(new FindingsStore(path).Groups()).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FixedFindingReopensWhenSeenAgain()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            var store = new FindingsStore(path);
            var finding = Real(Compare(P("Текст шаблона"), P("Текст файла")))[0];
            var record = new FolderRecord { Path = "C:\\A" };
            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            store.SetStatus(finding.GroupKey, FindingStatus.Fixed);
            store.ReplaceFolderResults(record, new[] { finding }, DateTime.Now);
            Assert.Equal(FindingStatus.New, Assert.Single(store.Groups()).Status);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
